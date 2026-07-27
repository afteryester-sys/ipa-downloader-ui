using System.Globalization;
using System.IO;
using System.Windows;
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

        AppLine.Text =
            $"Приложение «{request.AppName}» уже было скачано ранее. " +
            "Выберите, что сделать с существующим файлом.";

        FileNameLine.Text = Path.GetFileName(request.ExistingPath);
        FileMetaLine.Text =
            $"{FormatBytes(request.ExistingSizeBytes)} · сохранён " +
            request.ExistingModifiedLocal.ToString("d MMMM yyyy, HH:mm", CultureInfo.CurrentCulture);
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
        if (bytes <= 0) return "размер неизвестен";
        string[] units = ["Б", "КБ", "МБ", "ГБ"];
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
