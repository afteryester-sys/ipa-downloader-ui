using System.Windows;
using System.Windows.Input;
using IPAStudio.Core.Models;

namespace IPAStudio.App.Views;

public partial class HeicExportPromptDialog : Window
{
    public HeicExportConversionMode SelectedMode { get; private set; } = HeicExportConversionMode.PostTransfer;
    public bool DeleteOriginals => ChkDeleteOriginals.IsChecked == true;
    public bool RememberChoice => ChkRememberChoice.IsChecked == true;

    public HeicExportPromptDialog(HeicExportConversionMode defaultMode = HeicExportConversionMode.PostTransfer, bool defaultDeleteOriginals = true)
    {
        InitializeComponent();
        Owner = Application.Current?.MainWindow;

        switch (defaultMode)
        {
            case HeicExportConversionMode.DuringTransfer:
                RadioDuringTransfer.IsChecked = true;
                break;
            case HeicExportConversionMode.Disabled:
                RadioDisabled.IsChecked = true;
                break;
            default:
                RadioPostTransfer.IsChecked = true;
                break;
        }

        ChkDeleteOriginals.IsChecked = defaultDeleteOriginals;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (RadioDuringTransfer.IsChecked == true)
        {
            SelectedMode = HeicExportConversionMode.DuringTransfer;
        }
        else if (RadioDisabled.IsChecked == true)
        {
            SelectedMode = HeicExportConversionMode.Disabled;
        }
        else
        {
            SelectedMode = HeicExportConversionMode.PostTransfer;
        }

        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
