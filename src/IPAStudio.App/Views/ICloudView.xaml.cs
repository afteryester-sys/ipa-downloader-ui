using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using IPAStudio.App.ViewModels;

namespace IPAStudio.App.Views;

public partial class ICloudView : UserControl
{
    public ICloudView() => InitializeComponent();

    private ICloudViewModel? ViewModel => DataContext as ICloudViewModel;

    // PasswordBox deliberately does not support binding, so the value is pushed to the
    // viewmodel by hand. It is cleared there as soon as sign-in completes.
    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && sender is PasswordBox box) vm.Password = box.Password;
    }

    private void OnPasswordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (ViewModel is not { } vm) return;

        if (vm.SignInCommand.CanExecute(null)) vm.SignInCommand.Execute(null);
    }

    // The checkbox updates IsSelected through the binding; this just refreshes the
    // "N selected" counter and the Download button's enabled state.
    private void OnPhotoCheckChanged(object sender, RoutedEventArgs e)
        => ViewModel?.OnPhotoSelectionChanged();

    // Double-click and Enter both open a note. Selection alone deliberately does not: the
    // body costs a request, and arrowing through the list would fire one per note.
    private void OnNoteActivated(object sender, MouseButtonEventArgs e) => OpenSelectedNote(sender);

    private void OnNoteKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space)) return;
        OpenSelectedNote(sender);
        e.Handled = true;
    }

    private void OpenSelectedNote(object sender)
    {
        if (ViewModel is not { } vm) return;
        if (sender is not ListBox { SelectedItem: { } note }) return;

        if (vm.OpenNoteBodyCommand.CanExecute(note)) vm.OpenNoteBodyCommand.Execute(note);
    }
}
