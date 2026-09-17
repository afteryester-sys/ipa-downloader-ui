using CommunityToolkit.Mvvm.ComponentModel;
using IPAStudio.Core.Tools;

namespace IPAStudio.App.ViewModels;

/// <summary>
/// One local throughput problem shown in Settings, with its fix state.
/// </summary>
public sealed partial class ThroughputFindingViewModel : ObservableObject
{
    public ThroughputFindingViewModel(ThroughputFinding finding)
    {
        Kind = finding.Kind;
        Title = finding.Title;
        Detail = finding.Detail;
        CanAutoFix = finding.CanAutoFix;
    }

    /// <summary>Stable identifier used to apply the fix and to remember dismissals.</summary>
    public string Kind { get; }

    public string Title { get; }

    public string Detail { get; }

    /// <summary>True when the app can address this itself.</summary>
    public bool CanAutoFix { get; }

    [ObservableProperty]
    private bool _isFixing;

    /// <summary>
    /// Set when a fix attempt did not take effect. The reason varies — a dismissed
    /// elevation prompt, or a Defender managed by policy — and each needs different
    /// action from the user, so the wording lives in <see cref="FixMessage"/> instead
    /// of being one fixed string.
    /// </summary>
    [ObservableProperty]
    private bool _fixFailed;

    /// <summary>Why the last fix attempt did not take effect, and what to do about it.</summary>
    [ObservableProperty]
    private string _fixMessage = "";
}
