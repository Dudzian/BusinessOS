namespace BusinessOS.AppHost;

public sealed record DeferredShutdownResult(
    Exception? OperationException,
    Exception? CancellationException);

public sealed class RecoveryCloseIntent
{
    private readonly object sync = new();
    private bool closeRequested;
    private bool internalCloseAuthorized;

    public bool IsCloseRequested { get { lock (sync) return closeRequested; } }
    public bool IsInternalCloseAuthorized { get { lock (sync) return internalCloseAuthorized; } }

    public bool RequestClose()
    {
        lock (sync)
        {
            if (closeRequested) return false;
            closeRequested = true;
            return true;
        }
    }

    public bool TryAuthorizeInternalClose()
    {
        lock (sync)
        {
            if (closeRequested) return false;
            internalCloseAuthorized = true;
            return true;
        }
    }
}

public sealed class DeferredShutdownGate
{
    private readonly object sync = new();
    private Task activeOperation = Task.CompletedTask;
    private Task<DeferredShutdownResult>? waitTask;

    public void Track(Task operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (sync)
        {
            if (waitTask is not null) throw new InvalidOperationException("Shutdown has already been requested.");
            activeOperation = operation;
        }
    }

    public Task<DeferredShutdownResult> WaitForSafeShutdownAsync(Action cancel)
    {
        ArgumentNullException.ThrowIfNull(cancel);
        lock (sync)
        {
            if (waitTask is not null) return waitTask;
            waitTask = CancelAndWaitForOperationAsync(cancel, activeOperation);
            return waitTask;
        }
    }

    private static async Task<DeferredShutdownResult> CancelAndWaitForOperationAsync(Action cancel, Task operation)
    {
        Exception? cancellationException = null;
        try
        {
            cancel();
        }
        catch (Exception exception)
        {
            cancellationException = exception;
        }

        try
        {
            await operation.ConfigureAwait(false);
            return new(null, cancellationException);
        }
        catch (OperationCanceledException) when (operation.IsCanceled)
        {
            return new(null, cancellationException);
        }
        catch (Exception exception)
        {
            return new(exception, cancellationException);
        }
    }
}
