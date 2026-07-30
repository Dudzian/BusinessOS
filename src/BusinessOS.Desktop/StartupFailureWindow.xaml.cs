using BusinessOS.AppHost;
using Microsoft.UI.Xaml;

namespace BusinessOS.Desktop;

public sealed partial class StartupFailureWindow : Window
{
    private readonly Func<Task<ApplicationStartupResult>> retry;
    private readonly Action retrySucceeded;
    private readonly Func<Task> close;
    private readonly Func<Exception, ApplicationStartupResult> reportUnexpectedRetryFailure;
    private readonly Action openRecovery;
    private readonly Func<bool> canRecover;
    private bool isActive = true;
    private int operationStarted;

    public StartupFailureWindow(
        ApplicationStartupResult result,
        Func<Task<ApplicationStartupResult>> retry,
        Action retrySucceeded,
        Func<Task> close,
        Func<bool> canRecover,
        Action openRecovery,
        Func<Exception, ApplicationStartupResult> reportUnexpectedRetryFailure)
    {
        InitializeComponent();
        this.retry = retry;
        this.retrySucceeded = retrySucceeded;
        this.close = close;
        this.openRecovery = openRecovery;
        this.canRecover = canRecover;
        this.reportUnexpectedRetryFailure = reportUnexpectedRetryFailure;
        Closed += (_, _) => isActive = false;
        ShowResultAndCapabilities(result);
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (Interlocked.Exchange(ref operationStarted, 1) != 0) return;
        RetryButton.IsEnabled = false;
        RecoveryButton.IsEnabled = false;
        CloseButton.IsEnabled = false;
        try
        {
            var result = await retry().ConfigureAwait(true);
            if (!isActive) return;

            if (result.Succeeded)
            {
                retrySucceeded();
                return;
            }
            else
            {
                ShowResultAndCapabilities(result);
            }
        }
        catch (Exception exception)
        {
            if (isActive) ShowResultAndCapabilities(reportUnexpectedRetryFailure(exception));
        }
        finally
        {
            Interlocked.Exchange(ref operationStarted, 0);
            if (isActive)
            {
                RetryButton.IsEnabled = true;
                CloseButton.IsEnabled = true;
                RefreshRecoveryCapability();
            }
        }
    }

    private async void Close_Click(object sender, RoutedEventArgs e)
    {
        if (Interlocked.Exchange(ref operationStarted, 1) != 0) return;
        RetryButton.IsEnabled = RecoveryButton.IsEnabled = CloseButton.IsEnabled = false;
        await close().ConfigureAwait(true);
    }

    private void Recovery_Click(object sender, RoutedEventArgs e)
    {
        if (Interlocked.Exchange(ref operationStarted, 1) != 0) return;
        if (!canRecover()) { Interlocked.Exchange(ref operationStarted, 0); RefreshRecoveryCapability(); return; }
        RetryButton.IsEnabled = RecoveryButton.IsEnabled = CloseButton.IsEnabled = false;
        openRecovery();
    }

    private static bool CanRecover(ApplicationStartupResult result) => result.FailureCode is
        ApplicationStartupFailureCode.DatabaseInspectionFailed or ApplicationStartupFailureCode.BackupFailed or
        ApplicationStartupFailureCode.BackupIntegrityCheckFailed or ApplicationStartupFailureCode.MigrationFailed;

    private void ShowResultAndCapabilities(ApplicationStartupResult result)
    {
        UserMessageText.Text = result.UserMessage;
        DiagnosticIdText.Text = $"DiagnosticId: {result.DiagnosticId}";
        RecoveryButton.Visibility = IsPersistenceFailure(result) && canRecover() ? Visibility.Visible : Visibility.Collapsed;
        RefreshRecoveryCapability();
    }

    private void RefreshRecoveryCapability() => RecoveryButton.IsEnabled = isActive && Volatile.Read(ref operationStarted) == 0 && canRecover();

    private static bool IsPersistenceFailure(ApplicationStartupResult result) => CanRecover(result);
}
