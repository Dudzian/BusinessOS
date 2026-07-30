using BusinessOS.Desktop.ViewModels;
using Microsoft.UI.Xaml;

namespace BusinessOS.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly Action openRecovery;

    public MainWindow(MainViewModel viewModel, Action openRecovery)
    {
        InitializeComponent();
        Title = "BusinessOS";
        this.openRecovery = openRecovery;

        if (Content is FrameworkElement root)
        {
            root.DataContext = viewModel;
        }
    }

    private void Recovery_Click(object sender, RoutedEventArgs e) => openRecovery();
}
