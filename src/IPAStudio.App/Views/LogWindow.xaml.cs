using System.Windows;
using IPAStudio.App.ViewModels;

namespace IPAStudio.App.Views;

public partial class LogWindow : Window
{
    private readonly LogViewModel _vm;

    public LogWindow()
    {
        InitializeComponent();
        _vm = new LogViewModel();
        DataContext = _vm;

        // Keep the newest entries in view as they stream in.
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LogViewModel.Text) && _vm.AutoScroll)
                LogBox.ScrollToEnd();
        };

        Loaded += (_, _) => LogBox.ScrollToEnd();
        Closed += (_, _) => _vm.Dispose();
    }
}
