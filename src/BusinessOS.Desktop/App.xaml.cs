using System.Reflection;
using BusinessOS.AppHost;
using BusinessOS.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
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
        if (Volatile.Read(ref shutdownStarted) != 0) return;

        var activeHost = hostStartup.Host;
        if (activeHost is null || !hostStartup.HostStarted) return;

        var previous = window;
        var mainWindow = new MainWindow(ActivatorUtilities.CreateInstance<MainViewModel>(activeHost.Services));
        if (Volatile.Read(ref shutdownStarted) != 0 ||
            !ReferenceEquals(activeHost, hostStartup.Host) ||
            !hostStartup.HostStarted)
        {
            mainWindow.Close();
            return;
        }

        if (previous is not null) previous.Closed -= OnWindowClosed;
        window = mainWindow;
        mainWindow.Closed += OnWindowClosed;
        mainWindow.Activate();
        previous?.Close();
    }

    private void ShowFailure(ApplicationStartupResult result)
    {
        window = new StartupFailureWindow(
            result,
            EnsureHostAndPersistenceReadyAsync,
            ShowMainWindow,
            ShutdownAndExitAsync,
            exception => hostStartup.ReportUnexpectedFailure("Retry", "Nie udało się ponowić przygotowania bazy danych.", exception));
        window.Closed += OnWindowClosed;
        window.Activate();
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (ReferenceEquals(window, sender)) window = null;
        await ShutdownAndExitAsync().ConfigureAwait(true);
    }

    private async Task ShutdownAndExitAsync()
    {
        if (Interlocked.Exchange(ref shutdownStarted, 1) != 0) return;
        var currentWindow = window;
        window = null;
        if (currentWindow is not null) currentWindow.Closed -= OnWindowClosed;
        try
        {
            await hostStartup.ShutdownAsync(CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            currentWindow?.Close();
            Exit();
        }
    }
}
