using System.Text;
using System.Text.RegularExpressions;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Tools;
using iMobileDevice;
using iMobileDevice.iDevice;
using iMobileDevice.SyslogRelay;

namespace IPAStudio.Core.Services;

/// <summary>
/// How much of the device log to keep.
/// </summary>
public enum SyslogFilter
{
    /// <summary>Everything the device emits. Very noisy; useful only for a broad look.</summary>
    Everything,

    /// <summary>
    /// Only lines from the processes involved in installing an app and starting it:
    /// installd, the FairPlay daemon, code-signing, SpringBoard launch failures and the
    /// App Store daemons. This is the default because the noise floor of a full iOS log
    /// is high enough to bury the handful of lines that actually explain a failure.
    /// </summary>
    InstallAndLaunch,
}

/// <summary>One line of device log, with the classification used to colour it.</summary>
public sealed record SyslogLine(DateTimeOffset At, string Text, SyslogSeverity Severity);

public enum SyslogSeverity
{
    Normal,

    /// <summary>Worth reading, but not necessarily a fault.</summary>
    Notable,

    /// <summary>Names a concrete failure: FairPlay, code signing, or a refused install.</summary>
    Critical,
}

/// <summary>
/// Streams the live syslog off a connected device.
///
/// This exists because the failures that matter here are invisible from the Windows side.
/// When an app installs and then shows a white screen and closes, nothing about that is
/// reported back over USB: the install already returned success, and the launch failure
/// happens entirely on the phone. The only place it is stated out loud is the device's own
/// log, where the kernel says why it killed the process.
///
/// Uses <c>syslog_relay_receive_with_timeout</c> in a loop on a dedicated thread rather
/// than <c>syslog_relay_start_capture</c>. The capture API calls back from a native thread,
/// which means keeping a delegate alive for the lifetime of the client and being careful
/// about what runs in that callback; polling has neither problem and cannot outlive this
/// object.
/// </summary>
public sealed class DeviceSyslogService : IDisposable
{
    private const int MaxLines = 4000;

    private readonly object _sync = new();
    private readonly List<SyslogLine> _lines = new();

    private Thread? _worker;
    private CancellationTokenSource? _cts;

    /// <summary>Raised whenever new lines arrive, on the reader thread.</summary>
    public event Action? LinesAdded;

    /// <summary>Raised when the connection state changes. True while streaming.</summary>
    public event Action<bool, string>? StatusChanged;

    public bool IsRunning { get; private set; }

    /// <summary>UDID currently being followed, or null.</summary>
    public string? Udid { get; private set; }

    public SyslogFilter Filter { get; set; } = SyslogFilter.InstallAndLaunch;

    /// <summary>
    /// Set once a line has named a FairPlay decrypt failure, which is the signature of an
    /// app that installed cleanly and cannot launch because the device holds no usable
    /// licence for it.
    /// </summary>
    public bool SawFairPlayFailure { get; private set; }

    /// <summary>
    /// Apple ID the device used the last time it asked the store to authorise something, as
    /// the device itself named it. A licence is issued to one account, so this is the account
    /// the app has to have been downloaded under - and it is not necessarily the account
    /// signed in here. Null when the device has not said.
    /// </summary>
    public string? DeviceAccount { get; private set; }

    /// <summary>
    /// Whether appstored started fetching the licence by itself ("Will start fairplay
    /// recovery"). It does this within a second of a refused launch, which means the phone
    /// asks the store for the licence on its own - the App Store download people are told to
    /// perform by hand is only one way of prompting the very same request. When this is set,
    /// launching the app again is usually all that is left to do.
    /// </summary>
    public bool SawLicenceRecovery { get; private set; }

    /// <summary>
    /// How many times recovery has been started. One attempt means it is in progress and
    /// worth waiting for; a run of them means the store keeps refusing, and telling anyone
    /// to wait longer would be wrong - recovery asks for a licence for the account the phone
    /// is signed in to, so it can never produce one that belongs to a different account.
    /// </summary>
    public int LicenceRecoveryAttempts { get; private set; }

    /// <summary>
    /// Set when installd rejected the licence while installing, before the app was ever
    /// launched ("FairPlay check for SINF validity ... returned error"). The install still
    /// reports success, so this is the earliest honest warning available.
    /// </summary>
    public bool SawSinfRejectedAtInstall { get; private set; }

    /// <summary>Starts following a device. Stops any previous session first.</summary>
    public void Start(string udid)
    {
        Stop();

        Udid = udid;
        SawFairPlayFailure = false;
        SawLicenceRecovery = false;
        LicenceRecoveryAttempts = 0;
        SawSinfRejectedAtInstall = false;
        DeviceAccount = null;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _worker = new Thread(() => Run(udid, token))
        {
            IsBackground = true,
            Name = "device-syslog",
        };
        _worker.Start();
    }

    /// <summary>Stops following the device and joins the reader thread.</summary>
    public void Stop()
    {
        var cts = _cts;
        var worker = _worker;
        _cts = null;
        _worker = null;

        if (cts is null) return;

        try { cts.Cancel(); } catch { /* already gone */ }

        // The reader blocks for at most one poll interval, so this returns promptly. The
        // timeout is a backstop: a hung native call must not stop the window from closing.
        try { worker?.Join(TimeSpan.FromSeconds(3)); } catch { /* best effort */ }
        try { cts.Dispose(); } catch { /* best effort */ }

        IsRunning = false;
    }

    /// <summary>Drops every buffered line.</summary>
    public void Clear()
    {
        lock (_sync) _lines.Clear();
        SawFairPlayFailure = false;
        SawLicenceRecovery = false;
        LicenceRecoveryAttempts = 0;
        SawSinfRejectedAtInstall = false;
        DeviceAccount = null;
    }

    /// <summary>Snapshot of the buffer, oldest first.</summary>
    public IReadOnlyList<SyslogLine> Snapshot()
    {
        lock (_sync) return _lines.ToArray();
    }

    /// <summary>The buffer as plain text, ready to be copied or saved.</summary>
    public string SnapshotText()
    {
        var sb = new StringBuilder();
        foreach (var line in Snapshot())
            sb.Append(line.At.ToLocalTime().ToString("HH:mm:ss.fff")).Append("  ").AppendLine(line.Text);
        return sb.ToString();
    }

    // ---- reader ------------------------------------------------------------------

    private void Run(string udid, CancellationToken ct)
    {
        try
        {
            NativeDevice.EnsureLoaded();
        }
        catch (Exception ex)
        {
            Report(false, $"native libraries failed to load: {ex.Message}");
            return;
        }

        var api = LibiMobileDevice.Instance.SyslogRelay;

        // Reconnects on its own: the cable gets unplugged, the phone locks and drops the
        // service, or the relay simply ends. Without this the window would go quiet and
        // look broken with no indication that it had stopped listening.
        while (!ct.IsCancellationRequested)
        {
            iDeviceHandle? device = null;
            SyslogRelayClientHandle? client = null;

            try
            {
                var opened = NativeDevice.Open(udid, out device);
                if (opened != iDeviceError.Success)
                {
                    Report(false, $"device not reachable ({opened})");
                    if (!Wait(ct, 2000)) return;
                    continue;
                }

                var started = api.syslog_relay_client_start_service(device, out client, "IPAStudio");
                if (started != SyslogRelayError.Success)
                {
                    // Usually an untrusted or locked device: the relay is one of the
                    // services lockdown refuses until the phone is unlocked and paired.
                    Report(false, $"syslog service unavailable ({started}) — unlock the device and trust this computer");
                    if (!Wait(ct, 2500)) return;
                    continue;
                }

                Report(true, "streaming");
                Pump(api, client, ct);
            }
            catch (Exception ex)
            {
                Report(false, ex.Message);
                AppLog.Warn($"Device syslog reader error: {ex.Message}");
            }
            finally
            {
                try { client?.Dispose(); } catch { /* best effort */ }
                try { device?.Dispose(); } catch { /* best effort */ }
            }

            if (!ct.IsCancellationRequested && !Wait(ct, 1500)) return;
        }
    }

    private void Pump(ISyslogRelayApi api, SyslogRelayClientHandle client, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var pending = new StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            uint received = 0;

            // A one-second timeout keeps cancellation responsive on a quiet device while
            // still blocking rather than spinning.
            var err = api.syslog_relay_receive_with_timeout(
                client, buffer, (uint)buffer.Length, ref received, 1000);

            if (err == SyslogRelayError.Timeout) continue;

            if (err != SyslogRelayError.Success)
            {
                Report(false, $"relay ended ({err})");
                return;
            }

            if (received == 0) continue;

            // The relay is a byte stream, not a line stream: a read can stop mid-line and
            // a single read can carry several. Text is accumulated and only split on
            // newlines so a line is never reported cut in half.
            pending.Append(Encoding.UTF8.GetString(buffer, 0, (int)received));
            Drain(pending);
        }
    }

    private void Drain(StringBuilder pending)
    {
        var text = pending.ToString();
        var lastBreak = text.LastIndexOf('\n');
        if (lastBreak < 0)
        {
            // Guards against a device that emits a huge line with no newline: without this
            // the buffer would grow without limit.
            if (pending.Length > 64 * 1024) pending.Clear();
            return;
        }

        var complete = text[..lastBreak];
        pending.Remove(0, lastBreak + 1);

        var added = new List<SyslogLine>();
        foreach (var raw in complete.Split('\n'))
        {
            var line = raw.TrimEnd('\r', '\0').Trim();
            if (line.Length == 0) continue;

            var severity = Classify(line, out var interesting);

            // Read before the filter, so which account the device uses is learnt even when
            // the line announcing it would not have been kept.
            if (DeviceAccount is null)
            {
                var account = AccountName.Match(line);
                if (account.Success) DeviceAccount = account.Groups[1].Value;
            }

            // Licence outcomes are read before the filter too: installd announces its verdict
            // during the install, and that line is worth acting on whether or not it is shown.
            if (IsFairPlay(line))
            {
                // "Will start fairplay recovery" mentions fairplay and quotes the failing
                // status, so it used to be counted as another failure. It is the opposite:
                // the phone announcing that it is fetching the licence itself.
                if (line.Contains("fairplay recovery", StringComparison.OrdinalIgnoreCase))
                {
                    SawLicenceRecovery = true;
                    LicenceRecoveryAttempts++;
                }
                else if (line.Contains("SINF validity", StringComparison.OrdinalIgnoreCase))
                {
                    SawSinfRejectedAtInstall = true;
                }
                else if (severity == SyslogSeverity.Critical)
                {
                    SawFairPlayFailure = true;
                }
            }

            if (Filter == SyslogFilter.InstallAndLaunch && !interesting) continue;

            added.Add(new SyslogLine(DateTimeOffset.UtcNow, line, severity));
        }

        if (added.Count == 0) return;

        lock (_sync)
        {
            _lines.AddRange(added);
            if (_lines.Count > MaxLines) _lines.RemoveRange(0, _lines.Count - MaxLines);
        }

        LinesAdded?.Invoke();
    }

    // ---- classification ----------------------------------------------------------

    // Processes that have something to say about installing an app or starting one.
    private static readonly string[] Subsystems =
    {
        "installd", "mobile_installation", "mobile_installer", "MIInstaller", "MIFileManager",
        "fairplayd", "fpsd", "amfid", "AppleMobileFileIntegrity", "kernel",
        "SpringBoard", "backboardd", "itunesstored", "appstored", "storekitd",
        "mobileassetd", "lsd", "installcoordinationd", "storedownloadd",
    };

    // Phrases that name an outright failure. Kept narrow on purpose: a "critical" line is
    // meant to be the answer, so anything vague belongs in Notable instead.
    private static readonly string[] CriticalPhrases =
    {
        "fairplay", "FairPlay", "EXEC_EXIT_REASON", "exec_mach_imgact",
        "code signature", "codesign", "invalid signature", "CS_VALID",
        "Killing process", "killing process", "sinf", "SC_Info",
        "no valid license", "license", "DecryptionFailed", "cryptid",
        "failed to install", "Install failed", "InstallationFailed",
        "denied", "unable to launch", "launch failed", "crashed",
        "exited abnormally", "terminated with", "Untrusted",
    };

    private static readonly string[] NotablePhrases =
    {
        "install", "Install", "launch", "Launch", "entitlement",
        "provision", "account", "Account", "purchase", "download",
    };

    // appstored says "Performing authorization for account: name@example.com (UUID)" when it
    // asks the store to authorise an app. The trailing UUID and any brackets are excluded.
    private static readonly Regex AccountName =
        new(@"for account:\s*([^\s()]+@[^\s()]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ProcessPrefix =
        new(@"^\w{3}\s+\d+\s[\d:]+\s+\S+\s+([A-Za-z0-9_.\-]+)", RegexOptions.Compiled);

    /// <summary>
    /// Decides how prominent a line is, and whether it survives the install/launch filter.
    /// </summary>
    private static SyslogSeverity Classify(string line, out bool interesting)
    {
        var fromRelevantSubsystem = false;
        foreach (var s in Subsystems)
        {
            if (line.Contains(s, StringComparison.OrdinalIgnoreCase))
            {
                fromRelevantSubsystem = true;
                break;
            }
        }

        var critical = false;
        foreach (var p in CriticalPhrases)
        {
            if (line.Contains(p, StringComparison.OrdinalIgnoreCase))
            {
                critical = true;
                break;
            }
        }

        if (critical)
        {
            // A failure is kept whatever process reported it. Restricting these to the
            // known subsystem list would risk filtering out the one line that explains
            // the problem just because it came from somewhere unexpected.
            interesting = true;
            return SyslogSeverity.Critical;
        }

        var notable = false;
        foreach (var p in NotablePhrases)
        {
            if (line.Contains(p, StringComparison.OrdinalIgnoreCase))
            {
                notable = true;
                break;
            }
        }

        // Coming from a watched process is not on its own enough to survive the filter.
        // backboardd and SpringBoard are on that list because they report launches, but they
        // also narrate every brightness change - hundreds of lines a second, all of which used
        // to be kept as "install and launch" and flood the window until it stopped responding.
        // The severity below is unaffected: with the filter off these lines still show.
        interesting = fromRelevantSubsystem && notable;
        return interesting ? SyslogSeverity.Notable : SyslogSeverity.Normal;
    }

    private static bool IsFairPlay(string line) =>
        line.Contains("fairplay", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("EXEC_EXIT_REASON_FAIRPLAY", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("sinf", StringComparison.OrdinalIgnoreCase);

    /// <summary>Best-effort process name, used for nothing more than readability.</summary>
    public static string? ProcessOf(string line)
    {
        var m = ProcessPrefix.Match(line);
        return m.Success ? m.Groups[1].Value : null;
    }

    private void Report(bool running, string status)
    {
        IsRunning = running;
        StatusChanged?.Invoke(running, status);
    }

    private static bool Wait(CancellationToken ct, int ms)
    {
        try { return !ct.WaitHandle.WaitOne(ms); }
        catch { return false; }
    }

    public void Dispose() => Stop();
}
