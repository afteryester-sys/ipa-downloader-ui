using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using IPAStudio.App.ViewModels;
using IPAStudio.Core.Services;

namespace IPAStudio.App.Views;

public partial class DeviceLogWindow : Window
{
    private readonly DeviceLogViewModel _vm;

    public DeviceLogWindow(DeviceService devices, AuthService auth)
    {
        InitializeComponent();
        _vm = new DeviceLogViewModel(devices, auth);
        DataContext = _vm;

        // Follow the tail as lines stream in. Bound to the collection rather than a
        // property change because the list is rebuilt in place on every batch.
        ((INotifyCollectionChanged)_vm.Lines).CollectionChanged += (_, _) =>
        {
            if (!_vm.AutoScroll || _vm.Lines.Count == 0) return;
            LinesList.ScrollIntoView(_vm.Lines[^1]);
        };

        Closed += (_, _) => _vm.Dispose();
    }
}
