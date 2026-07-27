using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BusinessOS.AppHost;

public sealed class ApplicationHostStartup(Func<IHost> hostFactory)
{
    private IHost? host;

    public IHost? Host => host;
    public bool HostStarted { get; private set; }

    public async Task<ApplicationStartupResult> EnsureHostAndPersistenceReadyAsync(CancellationToken cancellationToken)
    {
        if (host is null)
        {
            try
            {
                host = hostFactory();
            }
            catch (Exception exception)
            {
                return ReportUnexpectedFailure("BuildHost", "Nie udało się uruchomić aplikacji.", exception);
            }
        }

        if (!HostStarted)
        {
            try
            {
                await host.StartAsync(cancellationToken).ConfigureAwait(false);
                HostStarted = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await ResetFailedHostWithoutMaskingAsync("StartHostCancelled").ConfigureAwait(false);
                throw;
            }
            catch (Exception exception)
            {
                await ResetFailedHostWithoutMaskingAsync("StartHostFailedCleanup").ConfigureAwait(false);
                return ReportUnexpectedFailure("StartHost", "Nie udało się uruchomić aplikacji.", exception);
            }
        }

        try
        {
            return await host.Services.GetRequiredService<IApplicationStartupCoordinator>()
                .InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            return ReportUnexpectedFailure("InitializePersistence", "Nie udało się przygotować bazy danych.", exception);
        }
    }

    public ApplicationStartupResult ReportUnexpectedFailure(string stage, string userMessage, Exception exception)
    {
        var diagnosticId = Guid.NewGuid().ToString("N");
        try
        {
            var logger = host?.Services.GetService<ILogger<ApplicationHostStartup>>();
            if (logger is not null)
            {
                logger.LogError(exception, "Unexpected application startup failure at {Stage}; DiagnosticId {DiagnosticId}.", stage, diagnosticId);
            }
            else
            {
                TraceFailure(stage, diagnosticId, exception);
            }
        }
        catch (Exception loggingException)
        {
            TraceFailure(stage, diagnosticId, exception);
            Trace.TraceError(
                "BusinessOS startup diagnostic logger failed at {0}; DiagnosticId {1}.{2}{3}",
                stage,
                diagnosticId,
                Environment.NewLine,
                loggingException);
        }

        return ApplicationStartupResult.Failure(
            ApplicationStartupFailureCode.UnexpectedFailure,
            userMessage,
            diagnosticId);
    }

    private async Task ResetFailedHostWithoutMaskingAsync(string stage)
    {
        try
        {
            if (host is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else host?.Dispose();
        }
        catch (Exception exception)
        {
            TraceFailure(stage, Guid.NewGuid().ToString("N"), exception);
        }
        finally
        {
            host = null;
            HostStarted = false;
        }
    }

    private static void TraceFailure(string stage, string diagnosticId, Exception exception) =>
        Trace.TraceError("BusinessOS bootstrap failure at {0}; DiagnosticId {1}.{2}{3}", stage, diagnosticId, Environment.NewLine, exception);
}
