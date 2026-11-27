using System.Windows;
using System.Windows.Controls;
using IPAStudio.App.ViewModels;

namespace IPAStudio.App.Views;

public partial class MediaConverterView : UserControl
{
    public MediaConverterView()
    {
        InitializeComponent();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0 && DataContext is MediaConverterViewModel vm)
            {
                vm.AddDroppedPaths(files);
            }
        }
    }
}
