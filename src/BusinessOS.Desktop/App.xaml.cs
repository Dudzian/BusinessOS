using System.Reflection;
using BusinessOS.AppHost;
using BusinessOS.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace BusinessOS.Desktop;

public partial class App : Application
{
    private enum RecoveryOrigin { MainWindow, StartupFailure }
    private readonly ApplicationHostStartup hostStartup;
    private Window? window;
    private int shutdownStarted;
    private ApplicationStartupResult? lastFailure;

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

        var mainWindow = new MainWindow(
            ActivatorUtilities.CreateInstance<MainViewModel>(activeHost.Services),
            new MainWorkspaceViewModel(
                ActivatorUtilities.CreateInstance<CompaniesViewModel>(activeHost.Services),
                ActivatorUtilities.CreateInstance<BusinessProjectsViewModel>(activeHost.Services),
                ActivatorUtilities.CreateInstance<BudgetingViewModel>(activeHost.Services),
                ActivatorUtilities.CreateInstance<ActualCostsViewModel>(activeHost.Services),
                ActivatorUtilities.CreateInstance<ForecastCostsViewModel>(activeHost.Services),
                ActivatorUtilities.CreateInstance<BudgetVarianceViewModel>(activeHost.Services),
                ActivatorUtilities.CreateInstance<BudgetForecastViewModel>(activeHost.Services),
                ActivatorUtilities.CreateInstance<CostCashFlowViewModel>(activeHost.Services)),
            () => ShowRecovery(RecoveryOrigin.MainWindow));
        if (Volatile.Read(ref shutdownStarted) != 0 ||
            !ReferenceEquals(activeHost, hostStartup.Host) ||
            !hostStartup.HostStarted)
        {
            mainWindow.Close();
            return;
        }

        TransitionToWindow(mainWindow);
    }

    private void ShowFailure(ApplicationStartupResult result)
    {
        lastFailure = result;
        var failureWindow = new StartupFailureWindow(
            result,
            EnsureHostAndPersistenceReadyAsync,
            ShowMainWindow,
            ShutdownAndExitAsync,
            CanRecover,
            () => ShowRecovery(RecoveryOrigin.StartupFailure),
            exception => hostStartup.ReportUnexpectedFailure("Retry", "Nie udało się ponowić przygotowania bazy danych.", exception));
        TransitionToWindow(failureWindow);
    }

    private bool CanRecover() =>
        Volatile.Read(ref shutdownStarted) == 0 && hostStartup.Host is not null && hostStartup.HostStarted &&
        hostStartup.Host.Services.GetService<ICompaniesRecoveryWorkflow>() is not null;

    private void ShowRecovery(RecoveryOrigin origin)
    {
        if (Volatile.Read(ref shutdownStarted) != 0 || hostStartup.Host is null || !hostStartup.HostStarted) return;
        var workflow = hostStartup.Host.Services.GetService<ICompaniesRecoveryWorkflow>();
        if (workflow is null) return;
        var recovery = new DatabaseRecoveryWindow(
            workflow,
            () => { if (origin == RecoveryOrigin.MainWindow) ShowMainWindow(); else if (lastFailure is not null) ShowFailure(lastFailure); },
            ShutdownAndExitAsync,
            EnsureHostAndPersistenceReadyAsync,
            CompleteRecovery);
        TransitionToWindow(recovery);
    }

    private void CompleteRecovery(ApplicationStartupResult result)
    {
        if (result.Succeeded) ShowMainWindow(); else ShowFailure(result);
    }

    private void TransitionToWindow(Window next)
    {
        if (Volatile.Read(ref shutdownStarted) != 0) { next.Close(); return; }
        var previous = window;
        if (previous is DatabaseRecoveryWindow recoveryWindow && !recoveryWindow.AuthorizeInternalClose())
        {
            next.Close();
            return;
        }
        if (previous is not null) previous.Closed -= OnWindowClosed;
        window = next;
        next.Closed += OnWindowClosed;
        next.Activate();
        previous?.Close();
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
