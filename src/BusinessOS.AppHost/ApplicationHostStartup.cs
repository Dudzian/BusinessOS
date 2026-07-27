using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BusinessOS.AppHost;

public sealed class ApplicationHostStartup(Func<IHost> hostFactory)
{
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly object shutdownLock = new();
    private IHost? host;
    private Task? shutdownTask;
    private bool shutdownRequested;

    public IHost? Host => host;
    public bool HostStarted { get; private set; }

    public async Task<ApplicationStartupResult> EnsureHostAndPersistenceReadyAsync(CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsShutdownRequested()) return ApplicationStartupResult.Cancelled();

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
                var result = await host.Services.GetRequiredService<IApplicationStartupCoordinator>()
                    .InitializeAsync(cancellationToken).ConfigureAwait(false);
                if (IsShutdownRequested()) return ApplicationStartupResult.Cancelled();
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                return ReportUnexpectedFailure("InitializePersistence", "Nie udało się przygotować bazy danych.", exception);
            }
        }
        finally
        {
            lifecycleGate.Release();
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

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        lock (shutdownLock)
        {
            shutdownRequested = true;
            return shutdownTask ??= ShutdownCoreAsync(cancellationToken);
        }
    }

    private async Task ShutdownCoreAsync(CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        IHost? hostToShutdown;
        try
        {
            hostToShutdown = host;
            host = null;
            HostStarted = false;
        }
        finally
        {
            lifecycleGate.Release();
        }

        if (hostToShutdown is null) return;

        var logger = GetShutdownLogger(hostToShutdown);
        try
        {
            await hostToShutdown.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogShutdownFailure(logger, "StopHost", exception);
        }
        finally
        {
            try
            {
                if (hostToShutdown is IAsyncDisposable asyncDisposable)
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                else
                    hostToShutdown.Dispose();
            }
            catch (Exception exception)
            {
                LogShutdownFailure(logger, "DisposeHost", exception);
            }
        }
    }

    private bool IsShutdownRequested()
    {
        lock (shutdownLock) return shutdownRequested;
    }

    private static ILogger<ApplicationHostStartup>? GetShutdownLogger(IHost hostToShutdown)
    {
        try { return hostToShutdown.Services.GetService<ILogger<ApplicationHostStartup>>(); }
        catch (Exception exception)
        {
            TraceFailure("ResolveShutdownLogger", Guid.NewGuid().ToString("N"), exception);
            return null;
        }
    }

    private static void LogShutdownFailure(ILogger<ApplicationHostStartup>? logger, string stage, Exception exception)
    {
        var diagnosticId = Guid.NewGuid().ToString("N");
        try
        {
            if (logger is not null)
                logger.LogError(exception, "Application host shutdown failure at {Stage}; DiagnosticId {DiagnosticId}.", stage, diagnosticId);
            else
                TraceFailure(stage, diagnosticId, exception);
        }
        catch (Exception loggingException)
        {
            TraceFailure(stage, diagnosticId, exception);
            TraceFailure("ShutdownLogger", diagnosticId, loggingException);
        }
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
