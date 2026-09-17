namespace IPAStudio.Core.Models;

/// <summary>
/// A single unit of work in the install queue: one app targeted at one device.
/// Progresses through <see cref="QueueStage"/> stages with per-stage progress.
/// </summary>
public sealed class QueueItem
{
    public required AppEntry App { get; init; }
    public required Device TargetDevice { get; init; }

    public QueueStage Stage { get; set; } = QueueStage.Pending;

    /// <summary>Progress of the current stage, 0-100.</summary>
    public double StageProgress { get; set; }

    /// <summary>Download speed in bytes/second, when downloading.</summary>
    public double DownloadSpeedBps { get; set; }

    /// <summary>Total bytes to download, when known.</summary>
    public long TotalBytes { get; set; }

    /// <summary>Bytes downloaded so far.</summary>
    public long DownloadedBytes { get; set; }

    /// <summary>
    /// True while ipatool is authenticating with Apple and no bytes have moved yet.
    /// There is no meaningful percentage during this phase, so the UI should show an
    /// indeterminate bar (bind <c>ProgressBar.IsIndeterminate</c> to this) together
    /// with the live elapsed counter in <see cref="StatusDetail"/>. Without it the bar
    /// sits at 0 and the download looks stuck.
    /// </summary>
    public bool IsConnecting { get; set; }

    /// <summary>
    /// True during the post-download "finalizing" phase (ipatool repackaging /
    /// injecting the license), so the UI can show a moving bar and "packaging"
    /// text instead of a bar frozen at ~99%.
    /// </summary>
    public bool IsFinalizing { get; set; }

    /// <summary>Human-readable status detail, e.g. "Installing (42%)".</summary>
    public string StatusDetail { get; set; } = "";

    /// <summary>Error message when <see cref="Stage"/> is <see cref="QueueStage.Failed"/>.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Number of retry attempts performed.</summary>
    public int RetryCount { get; set; }

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// When true the IPA file is already on disk (App.LocalIpaPath is set) and
    /// came from a direct file-picker selection, not from the App Store catalog.
    /// The queue skips Checking/Licensing/Downloading and goes straight to Installing.
    /// The install ignores the signed-in Apple ID — any IPA can be sideloaded this way.
    /// </summary>
    public bool IsDirectIpaInstall { get; init; }
}

/// <summary>
/// Ordered pipeline stages for a queue item. Terminal stages: Done, Failed, Cancelled.
/// </summary>
public enum QueueStage
{
    /// <summary>Waiting in the queue.</summary>
    Pending,

    /// <summary>Checking the local IPA cache. Does not touch the network.</summary>
    Checking,

    /// <summary>
    /// Obtaining a license (ipatool purchase). Only entered when a download reported
    /// that the Apple ID does not own the app — the normal path acquires the license
    /// as part of <c>download --purchase</c>.
    /// </summary>
    Licensing,

    /// <summary>Downloading the IPA (ipatool download).</summary>
    Downloading,

    /// <summary>Installing onto the device (ideviceinstaller install).</summary>
    Installing,

    /// <summary>Completed successfully.</summary>
    Done,

    /// <summary>Failed; see <see cref="QueueItem.ErrorMessage"/>.</summary>
    Failed,

    /// <summary>Cancelled by the user.</summary>
    Cancelled,
}
