using System.Diagnostics;
using System.Text;
using IPAStudio.Core.Diagnostics;

namespace IPAStudio.Core.Tools;

/// <summary>Result of a completed process run.</summary>
public sealed class ProcessResult
{
    public int ExitCode { get; init; }
    public string StdOut { get; init; } = "";
    public string StdErr { get; init; } = "";
    public bool Success => ExitCode == 0;
    public string CombinedOutput => string.IsNullOrEmpty(StdErr) ? StdOut : StdOut + Environment.NewLine + StdErr;
}

/// <summary>
/// Runs external tools (ipatool, ideviceinstaller, ...) with real-time
/// line streaming for progress parsing, optional stdin input (2FA codes),
/// and cancellation support.
/// </summary>
public sealed class ProcessRunner
{
    private readonly ProcessJobObject? _job;

    /// <summary>
    /// The <paramref name="job"/> (optional) ties every spawned process to a Windows
    /// Job Object so they are all killed when the app exits, leaving no orphaned tools
    /// holding the portable folder open.
    /// </summary>
    public ProcessRunner(ProcessJobObject? job = null)
    {
        _job = job;
    }

    /// <summary>
    /// Runs a process to completion.
    /// </summary>
    /// <param name="fileName">Executable path.</param>
    /// <param name="arguments">Argument list (safely escaped per-item).</param>
    /// <param name="onOutputLine">Callback for each stdout line, invoked in real time.</param>
    /// <param name="onErrorLine">Callback for each stderr line, invoked in real time.</param>
    /// <param name="stdinLines">Lines written to stdin after start (e.g. a 2FA code).</param>
    /// <param name="onStdinReady">
    /// Invoked once right after the process starts, receiving the live stdin writer.
    /// Use this for interactive tools (e.g. ipatool) that prompt on stdout/stderr and
    /// wait for a reply on stdin. The writer stays open until the process exits.
    /// </param>
    /// <param name="environment">Extra environment variables.</param>
    /// <param name="quiet">
    /// Marks this run as routine background polling. The RUN/EXIT lines drop to Debug
    /// level (hidden unless verbose logging is on) so a poll loop firing every few
    /// seconds cannot bury real events. Failures and stderr are still reported at their
    /// normal level regardless — quiet suppresses noise, never problems.
    /// </param>
    /// <param name="ct">Cancellation token; kills the process tree when cancelled.</param>
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        Action<string>? onOutputLine = null,
        Action<string>? onErrorLine = null,
        IReadOnlyList<string>? stdinLines = null,
        Action<StreamWriter>? onStdinReady = null,
        IReadOnlyDictionary<string, string>? environment = null,
        bool closeStdin = false,
        string? workingDirectory = null,
        bool quiet = false,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = closeStdin || stdinLines is { Count: > 0 } || onStdinReady is not null,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            // Ensure side-by-side helpers (e.g. anisette.exe for ipatool v3) are
            // discoverable — set the working directory to the tool's own folder.
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(fileName) ?? "",
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        if (environment is not null)
            foreach (var (key, value) in environment)
                psi.Environment[key] = value;

        if (quiet)
            AppLog.Debug(() => $"RUN {ExeName(fileName)} {string.Join(' ', arguments)}");
        else
            AppLog.Info($"RUN {ExeName(fileName)} {string.Join(' ', arguments)}");

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdout.AppendLine(e.Data);
            onOutputLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderr.AppendLine(e.Data);
            onErrorLine?.Invoke(e.Data);
        };

        if (!process.Start())
        {
            AppLog.Error($"Failed to start process: {fileName}");
            throw new InvalidOperationException($"Failed to start process: {fileName}");
        }

        _job?.Track(process);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (onStdinReady is not null)
        {
            // Hand the live stdin writer to the caller for interactive prompts.
            onStdinReady(process.StandardInput);
        }
        else if (stdinLines is { Count: > 0 })
        {
            foreach (var line in stdinLines)
                await process.StandardInput.WriteLineAsync(line).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        else if (closeStdin)
        {
            // Close stdin immediately so any interactive prompt (e.g. ipatool's
            // "Enter 2FA code: ") receives EOF and falls back to its non-interactive
            // path instead of blocking forever waiting for input.
            process.StandardInput.Close();
        }

        await using var registration = ct.Register(() => KillTree(process));

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var result = new ProcessResult
        {
            ExitCode = process.ExitCode,
            StdOut = stdout.ToString(),
            StdErr = stderr.ToString(),
        };

        // A failure is never routine: if the process exited non-zero, log the EXIT at
        // Info even for a quiet run, otherwise the accompanying warnings below would
        // appear with no indication of which command produced them.
        if (quiet && result.Success)
            AppLog.Debug(() => $"EXIT {ExeName(fileName)} code={result.ExitCode}");
        else
            AppLog.Info($"EXIT {ExeName(fileName)} code={result.ExitCode}");

        var err = result.StdErr.Trim();
        if (err.Length > 0)
        {
            // Successful polls routinely write harmless notices to stderr; only treat
            // stderr as a warning when the command actually failed.
            if (quiet && result.Success)
                AppLog.Debug(() => $"  stderr: {Truncate(err, 1500)}");
            else
                AppLog.Warn($"  stderr: {Truncate(err, 1500)}");
        }

        if (!result.Success && result.StdOut.Trim().Length > 0)
            AppLog.Warn($"  stdout: {Truncate(result.StdOut.Trim(), 1500)}");

        return result;
    }

    /// <summary>
    /// Runs a process while streaming stdout/stderr <b>character by character</b> and
    /// flushing a segment on <c>\r</c> as well as <c>\n</c>.
    ///
    /// This is required for tools that render an in-place progress bar (ipatool uses
    /// a carriage-return bar for <c>download</c>). The line-oriented
    /// <see cref="RunAsync"/> uses <see cref="Process.BeginOutputReadLine"/>, which
    /// only yields data on a newline — so a CR-updated bar produces NO callbacks at
    /// all until the process exits, and live progress parsing silently never fires.
    /// </summary>
    /// <param name="onSegment">
    /// Invoked for every flushed segment (a completed line, or one progress-bar frame).
    /// Receives stdout and stderr segments interleaved as they arrive.
    /// </param>
    public async Task<ProcessResult> RunStreamingAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        Action<string>? onSegment = null,
        IReadOnlyDictionary<string, string>? environment = null,
        string? workingDirectory = null,
        bool closeStdin = true,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(fileName) ?? "",
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        if (environment is not null)
            foreach (var (key, value) in environment)
                psi.Environment[key] = value;

        AppLog.Info($"RUN {ExeName(fileName)} {string.Join(' ', arguments)}");

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var sync = new object();

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        if (!process.Start())
        {
            AppLog.Error($"Failed to start process: {fileName}");
            throw new InvalidOperationException($"Failed to start process: {fileName}");
        }

        _job?.Track(process);

        if (closeStdin)
        {
            // EOF on stdin so a stray interactive prompt cannot deadlock the run.
            try { process.StandardInput.Close(); } catch { /* already closed */ }
        }

        async Task PumpAsync(StreamReader reader, StringBuilder sink)
        {
            var buffer = new char[1024];
            var segment = new StringBuilder(256);

            void Flush()
            {
                if (segment.Length == 0) return;
                var text = segment.ToString();
                segment.Clear();
                try { onSegment?.Invoke(text); } catch { /* parser must never kill the pump */ }
            }

            while (true)
            {
                int n;
                try
                {
                    // Deliberately NOT passing ct: cancellation kills the process, which
                    // closes the pipe and ends the read naturally. Passing ct here would
                    // abandon the reader mid-buffer and lose the tail of the output.
                    n = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }
                if (n <= 0) break;

                lock (sync) sink.Append(buffer, 0, n);

                for (var i = 0; i < n; i++)
                {
                    var ch = buffer[i];
                    if (ch is '\r' or '\n')
                    {
                        Flush();
                    }
                    else
                    {
                        segment.Append(ch);
                        // Guard against a tool that never emits CR/LF at all.
                        if (segment.Length >= 4096) Flush();
                    }
                }
            }

            Flush();
        }

        var pumpOut = PumpAsync(process.StandardOutput, stdout);
        var pumpErr = PumpAsync(process.StandardError, stderr);

        await using var registration = ct.Register(() => KillTree(process));

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        try { await Task.WhenAll(pumpOut, pumpErr).ConfigureAwait(false); } catch { /* pipes closed */ }
        ct.ThrowIfCancellationRequested();

        var result = new ProcessResult
        {
            ExitCode = process.ExitCode,
            StdOut = stdout.ToString(),
            StdErr = stderr.ToString(),
        };

        AppLog.Info($"EXIT {ExeName(fileName)} code={result.ExitCode}");
        if (!result.Success)
        {
            var tail = result.CombinedOutput.Trim();
            if (tail.Length > 0) AppLog.Warn($"  output: {Truncate(tail, 1500)}");
        }

        return result;
    }

    /// <summary>
    /// Kills the whole process tree and <b>waits for it to actually exit</b>.
    ///
    /// Without the wait, <see cref="Process.Kill(bool)"/> returns while the child is
    /// still terminating and still holding its output file open. The next attempt then
    /// fails to delete the stale partial .ipa, and the progress poller reads that
    /// leftover file and reports an instant bogus 100%.
    /// </summary>
    private static void KillTree(Process process)
    {
        try
        {
            if (process.HasExited) return;
            process.Kill(entireProcessTree: true);
            process.WaitForExit(3000);
        }
        catch
        {
            // Process may have exited between the check and the kill.
        }
    }

    private static string ExeName(string path)
    {
        try { return Path.GetFileName(path); } catch { return path; }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + $"… (+{s.Length - max} chars)";

    /// <summary>
    /// Runs an interactive tool, reading stdout/stderr <b>character by character</b>
    /// so that prompts that are printed WITHOUT a trailing newline (e.g. ipatool's
    /// "2FA code: ") are surfaced immediately. Line-buffered reads would deadlock on
    /// such prompts because the newline never arrives until input is provided.
    /// </summary>
    /// <param name="onData">
    /// Called whenever new characters arrive, with the full combined (stdout+stderr)
    /// buffer so far and the live stdin writer. Use it to detect a prompt and reply.
    /// </param>
    public async Task<ProcessResult> RunInteractiveAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        Action<string, StreamWriter>? onData = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            // Set the working directory to the tool's folder so side-by-side
            // helpers (e.g. anisette.exe for ipatool v3) are found by Windows
            // when the tool calls CreateProcess("anisette.exe", ...) without
            // a full path — Windows searches CWD before PATH.
            WorkingDirectory = Path.GetDirectoryName(fileName) ?? "",
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        if (environment is not null)
            foreach (var (key, value) in environment)
                psi.Environment[key] = value;

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var combined = new StringBuilder();
        var sync = new object();

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start process: {fileName}");

        _job?.Track(process);

        var stdin = process.StandardInput;

        async Task PumpAsync(StreamReader reader, StringBuilder ownSink)
        {
            var buffer = new char[512];
            while (true)
            {
                int n;
                try
                {
                    n = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }
                if (n <= 0) break;

                var chunk = new string(buffer, 0, n);
                string snapshot;
                lock (sync)
                {
                    ownSink.Append(chunk);
                    combined.Append(chunk);
                    snapshot = combined.ToString();
                }
                onData?.Invoke(snapshot, stdin);
            }
        }

        var pumpOut = PumpAsync(process.StandardOutput, stdout);
        var pumpErr = PumpAsync(process.StandardError, stderr);

        await using var registration = ct.Register(() => KillTree(process));

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        try { await Task.WhenAll(pumpOut, pumpErr).ConfigureAwait(false); } catch { }
        ct.ThrowIfCancellationRequested();

        return new ProcessResult
        {
            ExitCode = process.ExitCode,
            StdOut = stdout.ToString(),
            StdErr = stderr.ToString(),
        };
    }
}
