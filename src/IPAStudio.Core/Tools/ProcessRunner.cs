using System.Diagnostics;
using System.Text;

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
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdinLines is { Count: > 0 } || onStdinReady is not null,
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
            throw new InvalidOperationException($"Failed to start process: {fileName}");

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

        return new ProcessResult
        {
            ExitCode = process.ExitCode,
            StdOut = stdout.ToString(),
            StdErr = stderr.ToString(),
        };
    }
}
