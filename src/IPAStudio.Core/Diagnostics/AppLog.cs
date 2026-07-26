using System.Text;
using System.Text.RegularExpressions;

namespace IPAStudio.Core.Diagnostics;

/// <summary>
/// Lightweight, thread-safe application logger. Keeps the most recent lines in an
/// in-memory ring buffer (for the in-app "Logs" viewer) and also appends everything
/// to a daily log file under %LOCALAPPDATA%\IPAStudio\logs so it never locks the
/// (portable) application folder. Secrets such as passwords are redacted.
/// </summary>
public static class AppLog
{
    private const int MaxLines = 5000;

    private static readonly object _sync = new();
    private static readonly LinkedList<string> _buffer = new();

    /// <summary>Raised (with the full snapshot) whenever a line is written.</summary>
    public static event Action? Changed;

    /// <summary>Absolute path of the current day's log file.</summary>
    public static string FilePath { get; }

    /// <summary>
    /// When false (the default), <see cref="Debug"/> lines are discarded entirely:
    /// never buffered, never written to disk, and the message is never even formatted.
    ///
    /// This exists because routine background chatter drowned out real events. Device
    /// discovery polls every few seconds and shells out to idevice_id/ideviceinfo on
    /// each pass, so the RUN/EXIT pair for those calls produced a wall of identical
    /// lines every few seconds. Those calls now log at Debug level, leaving the default
    /// log to actual state changes, warnings and errors.
    ///
    /// Set from the user's "Подробные логи" setting at startup.
    /// </summary>
    public static volatile bool Verbose;

    static AppLog()
    {
        string dir;
        try
        {
            dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "IPAStudio", "logs");
            Directory.CreateDirectory(dir);
        }
        catch
        {
            dir = Path.GetTempPath();
        }

        FilePath = Path.Combine(dir, $"ipastudio-{DateTime.Now:yyyy-MM-dd}.log");

        Info("================ IPA Studio session started ================");
        try
        {
            Info($"OS: {Environment.OSVersion}  |  64-bit: {Environment.Is64BitOperatingSystem}");
            Info($"Log file: {FilePath}");
        }
        catch { /* ignore */ }
    }

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);

    /// <summary>
    /// Routine, high-frequency detail. Dropped unless <see cref="Verbose"/> is on.
    /// </summary>
    public static void Debug(string message)
    {
        if (Verbose) Write("DEBUG", message);
    }

    /// <summary>
    /// Deferred variant: the factory only runs when verbose logging is enabled, so
    /// callers on hot paths pay nothing (no string interpolation, no allocation) while
    /// logging is off. Prefer this when building the message costs anything.
    /// </summary>
    public static void Debug(Func<string> message)
    {
        if (!Verbose) return;
        try { Write("DEBUG", message()); }
        catch { /* logging must never throw */ }
    }

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    /// <summary>Returns the entire in-memory buffer as a single string.</summary>
    public static string Snapshot()
    {
        lock (_sync)
        {
            var sb = new StringBuilder(_buffer.Count * 64);
            foreach (var line in _buffer) sb.AppendLine(line);
            return sb.ToString();
        }
    }

    /// <summary>Clears the in-memory buffer and truncates the log file.</summary>
    public static void Clear()
    {
        lock (_sync)
        {
            _buffer.Clear();
            try { File.WriteAllText(FilePath, ""); } catch { /* ignore */ }
        }
        Info("---- log cleared ----");
        Changed?.Invoke();
    }

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {Redact(message)}";

        lock (_sync)
        {
            // Split multi-line payloads (e.g. exceptions) into individual buffer lines.
            foreach (var part in line.Split('\n'))
                _buffer.AddLast(part.TrimEnd('\r'));

            while (_buffer.Count > MaxLines) _buffer.RemoveFirst();

            try { File.AppendAllText(FilePath, line + Environment.NewLine); }
            catch { /* logging must never throw */ }
        }

        Changed?.Invoke();
    }

    /// <summary>Masks credentials that may appear in logged command lines.</summary>
    private static string Redact(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        // "-p secret" / "--password secret"
        s = Regex.Replace(s, @"(?i)(\s(?:-p|--password)\s+)(\S+)", "$1********");
        return s;
    }
}
