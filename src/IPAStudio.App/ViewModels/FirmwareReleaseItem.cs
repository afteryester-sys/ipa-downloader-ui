using CommunityToolkit.Mvvm.ComponentModel;
using IPAStudio.Core.Models;

namespace IPAStudio.App.ViewModels;

public sealed partial class FirmwareReleaseItem : ObservableObject
{
    private readonly Action? _onSelectionChanged;

    public FirmwareRelease Release { get; }

    [ObservableProperty]
    private bool _isSelected;

    public FirmwareReleaseItem(FirmwareRelease release, Action? onSelectionChanged = null)
    {
        Release = release;
        _onSelectionChanged = onSelectionChanged;
    }

    partial void OnIsSelectedChanged(bool value)
    {
        _onSelectionChanged?.Invoke();
    }

    public string Identifier => Release.Identifier;
    public string Version => Release.Version;
    public string BuildId => Release.BuildId;
    public string? Sha1 => Release.Sha1;
    public string? Md5 => Release.Md5;
    public long FileSize => Release.FileSize;
    public string Url => Release.Url;
    public DateTimeOffset? ReleaseDate => Release.ReleaseDate;
    public DateTimeOffset? UploadDate => Release.UploadDate;
    public bool Signed => Release.Signed;
    public string SizeText => Release.SizeText;
    public string StatusText => Release.StatusText;
    public string ReleaseDateText => Release.ReleaseDateText;
}
