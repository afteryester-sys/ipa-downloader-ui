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

    /// <summary>Human-readable status detail, e.g. "Installing (42%)".</summary>
    public string StatusDetail { get; set; } = "";

    /// <summary>Error message when <see cref="Stage"/> is <see cref="QueueStage.Failed"/>.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Number of retry attempts performed.</summary>
    public int RetryCount { get; set; }

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>
/// Ordered pipeline stages for a queue item. Terminal stages: Done, Failed, Cancelled.
/// </summary>
public enum QueueStage
{
    /// <summary>Waiting in the queue.</summary>
    Pending,

    /// <summary>Checking local IPA cache and account license.</summary>
    Checking,

    /// <summary>Obtaining a license (ipatool purchase) because the account does not own the app.</summary>
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
