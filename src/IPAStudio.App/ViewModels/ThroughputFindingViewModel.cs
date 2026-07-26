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
    /// Set when a fix attempt did not take effect — most often because the user
    /// dismissed the UAC prompt, or because Defender is managed by group policy.
    /// </summary>
    [ObservableProperty]
    private bool _fixFailed;
}
