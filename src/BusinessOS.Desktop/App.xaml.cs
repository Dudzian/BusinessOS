using System.Reflection;
using BusinessOS.AppHost;
using BusinessOS.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace BusinessOS.Desktop;

public partial class App : Application
{
    private readonly ApplicationHostStartup hostStartup;
    private Window? window;
    private int shutdownStarted;

    public App()
    {
        InitializeComponent();
        hostStartup = new ApplicationHostStartup(() => BusinessOsHost.BuildHost(Assembly.GetExecutingAssembly()));
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var result = await EnsureHostAndPersistenceReadyAsync().ConfigureAwait(true);
            if (result.Succeeded) ShowMainWindow(); else ShowFailure(result);
        }
        catch (Exception exception)
        {
            var failure = hostStartup.ReportUnexpectedFailure("OnLaunched", "Nie udało się uruchomić aplikacji.", exception);
            try
            {
                ShowFailure(failure);
            }
            catch (Exception presentationException)
            {
                _ = hostStartup.ReportUnexpectedFailure("ShowFailure", "Nie udało się wyświetlić okna błędu.", presentationException);
            }
        }
    }

    private Task<ApplicationStartupResult> EnsureHostAndPersistenceReadyAsync() =>
        hostStartup.EnsureHostAndPersistenceReadyAsync(CancellationToken.None);

    private void ShowMainWindow()
    {
        var previous = window;
        window = new MainWindow(ActivatorUtilities.CreateInstance<MainViewModel>(hostStartup.Host!.Services));
        window.Closed += OnWindowClosed;
        window.Activate();
        if (previous is not null) { previous.Closed -= OnWindowClosed; previous.Close(); }
    }

    private void ShowFailure(ApplicationStartupResult result)
    {
        window = new StartupFailureWindow(
            result,
            EnsureHostAndPersistenceReadyAsync,
            ShowMainWindow,
            ShutdownAndCloseAsync,
            exception => hostStartup.ReportUnexpectedFailure("Retry", "Nie udało się ponowić przygotowania bazy danych.", exception));
        window.Closed += OnWindowClosed;
        window.Activate();
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args) => await ShutdownAsync().ConfigureAwait(true);

    private async Task ShutdownAndCloseAsync()
    {
        await ShutdownAsync().ConfigureAwait(true);
        window?.Close();
    }

    private async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref shutdownStarted, 1) != 0) return;
        var host = hostStartup.Host;
        if (host is null) return;
        try { await host.StopAsync().ConfigureAwait(true); }
        catch (Exception exception) { host.Services.GetRequiredService<ILogger<App>>().LogError(exception, "Host shutdown failed."); }
        finally { host.Dispose(); }
    }
}
