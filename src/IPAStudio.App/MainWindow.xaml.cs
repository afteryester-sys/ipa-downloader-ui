using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Animation;
using IPAStudio.App.ViewModels;

namespace IPAStudio.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookShell();
    }

    private void HookShell()
    {
        if (DataContext is not ShellViewModel shell) return;
        shell.PropertyChanged += OnShellPropertyChanged;
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ShellViewModel.CurrentViewModel)) return;

        // Fade + slide-up transition on every page change.
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var slide = new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        PageHost.BeginAnimation(OpacityProperty, fade);
        PageTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slide);
    }
}
