using BusinessOS.AppHost;
using BusinessOS.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;

namespace BusinessOS.Desktop;

public partial class App : Application
{
    private readonly IHost host;
    private Window? window;

    public App()
    {
        InitializeComponent();
        host = BusinessOsHost.BuildHost();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        await host.StartAsync().ConfigureAwait(true);
        window = new MainWindow(ActivatorUtilities.CreateInstance<MainViewModel>(host.Services));
        window.Closed += OnWindowClosed;
        window.Activate();
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        await host.StopAsync().ConfigureAwait(true);
        host.Dispose();
    }
}
