using System.Globalization;
using System.IO;
using System.Windows;
using IPAStudio.Core.Localization;
using IPAStudio.Core.Services;

namespace IPAStudio.App.Views;

/// <summary>
/// Asks the user what to do when a download would overwrite an existing .ipa.
///
/// Deliberately code-behind rather than MVVM: it is a modal, three-button prompt with
/// no state worth binding, and it must be creatable from a plain callback without
/// pulling a view model out of the container.
/// </summary>
public partial class FileConflictDialog : Window
{
    /// <summary>
    /// Chosen action. Defaults to KeepBoth so closing the window with Alt+F4 — or any
    /// path that bypasses the buttons — cannot destroy the existing file.
    /// </summary>
    public FileConflictDecision Decision { get; private set; } = FileConflictDecision.KeepBoth;

    /// <summary>True when the answer should apply to the rest of the queue.</summary>
    public bool ApplyToAll => ApplyToAllBox.IsChecked == true;

    public FileConflictDialog(FileConflictRequest request)
    {
        InitializeComponent();

        AppLine.Text = Loc.Format("L.Conflict.AppLine", request.AppName);

        FileNameLine.Text = Path.GetFileName(request.ExistingPath);
        FileMetaLine.Text = Loc.Format(
            "L.Conflict.FileMeta",
            FormatBytes(request.ExistingSizeBytes),
            request.ExistingModifiedLocal.ToString("d MMMM yyyy, HH:mm", CultureInfo.CurrentCulture));
    }

    private void OnReplace(object sender, RoutedEventArgs e) => Close(FileConflictDecision.Replace);

    private void OnKeepBoth(object sender, RoutedEventArgs e) => Close(FileConflictDecision.KeepBoth);

    private void OnCancel(object sender, RoutedEventArgs e) => Close(FileConflictDecision.Cancel);

    private void Close(FileConflictDecision decision)
    {
        Decision = decision;
        DialogResult = true;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return Loc.Get("L.Conflict.SizeUnknown");
        string[] units =
        [
            Loc.Get("L.Unit.B"), Loc.Get("L.Unit.KB"),
            Loc.Get("L.Unit.MB"), Loc.Get("L.Unit.GB"),
        ];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return string.Format(CultureInfo.CurrentCulture, "{0:0.#} {1}", value, units[unit]);
    }
}
