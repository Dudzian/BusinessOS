using BusinessOS.AppHost;
using Microsoft.UI.Xaml;

namespace BusinessOS.Desktop;

public sealed partial class StartupFailureWindow : Window
{
    private readonly Func<Task<ApplicationStartupResult>> retry;
    private readonly Action retrySucceeded;
    private readonly Func<Task> close;
    private readonly Func<Exception, ApplicationStartupResult> reportUnexpectedRetryFailure;
    private bool isActive = true;

    public StartupFailureWindow(
        ApplicationStartupResult result,
        Func<Task<ApplicationStartupResult>> retry,
        Action retrySucceeded,
        Func<Task> close,
        Func<Exception, ApplicationStartupResult> reportUnexpectedRetryFailure)
    {
        InitializeComponent();
        this.retry = retry;
        this.retrySucceeded = retrySucceeded;
        this.close = close;
        this.reportUnexpectedRetryFailure = reportUnexpectedRetryFailure;
        Closed += (_, _) => isActive = false;
        ShowResult(result);
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        RetryButton.IsEnabled = false;
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
                ShowResult(result);
            }
        }
        catch (Exception exception)
        {
            if (isActive) ShowResult(reportUnexpectedRetryFailure(exception));
        }
        finally
        {
            if (isActive)
            {
                RetryButton.IsEnabled = true;
                CloseButton.IsEnabled = true;
            }
        }
    }

    private async void Close_Click(object sender, RoutedEventArgs e) => await close().ConfigureAwait(true);

    private void ShowResult(ApplicationStartupResult result)
    {
        UserMessageText.Text = result.UserMessage;
        DiagnosticIdText.Text = $"DiagnosticId: {result.DiagnosticId}";
    }
}
