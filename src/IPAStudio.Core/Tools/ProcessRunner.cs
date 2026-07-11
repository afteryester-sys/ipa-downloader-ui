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
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        if (environment is not null)
            foreach (var (key, value) in environment)
                psi.Environment[key] = value;

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

        await using var registration = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Process may have exited between the check and the kill.
            }
        });

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var result = new ProcessResult
        {
            ExitCode = process.ExitCode,
            StdOut = stdout.ToString(),
            StdErr = stderr.ToString(),
        };

        AppLog.Info($"EXIT {ExeName(fileName)} code={result.ExitCode}");
        var err = result.StdErr.Trim();
        if (err.Length > 0)
            AppLog.Warn($"  stderr: {Truncate(err, 1500)}");
        if (!result.Success && result.StdOut.Trim().Length > 0)
            AppLog.Warn($"  stdout: {Truncate(result.StdOut.Trim(), 1500)}");

        return result;
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

        await using var registration = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { /* already exited */ }
        });

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
