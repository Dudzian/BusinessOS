using BusinessOS.Desktop.ViewModels;
using Microsoft.UI.Xaml;

namespace BusinessOS.Desktop;

public sealed partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        Title = "BusinessOS";

        if (Content is FrameworkElement root)
        {
            root.DataContext = viewModel;
        }
    }
}
