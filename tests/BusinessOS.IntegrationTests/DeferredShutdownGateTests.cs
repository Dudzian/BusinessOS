using System.Collections.Concurrent;
using BusinessOS.AppHost;
using FluentAssertions;
using Xunit;

namespace BusinessOS.IntegrationTests;

public sealed class DeferredShutdownGateTests
{
    [Fact]
    public async Task Shutdown_after_active_operation_runs_on_requested_UI_context()
    {
        using var context = new DedicatedSynchronizationContext();
        await context.RunAsync(async () =>
        {
            var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new DeferredShutdownGate();
            gate.Track(operation.Task);
            var callbackThread = -1; var calls = 0;

            var close = CloseOnCapturedContextAsync();
            await Task.Run(operation.SetResult);
            await close;

            callbackThread.Should().Be(context.ThreadId);
            calls.Should().Be(1);

            async Task CloseOnCapturedContextAsync()
            {
                await gate.WaitForSafeShutdownAsync(() => { });
                callbackThread = Environment.CurrentManagedThreadId;
                calls++;
            }
        });
    }

    [Fact]
    public async Task Faulted_operation_still_allows_single_shutdown()
    {
        var failure = new InvalidOperationException("controlled operation failure");
        var gate = new DeferredShutdownGate();
        gate.Track(Task.FromException(failure));
        var cancellations = 0;

        var result = await gate.WaitForSafeShutdownAsync(() => cancellations++);

        result.OperationException.Should().BeSameAs(failure);
        cancellations.Should().Be(1);
    }

    [Fact]
    public async Task Cancelled_operation_allows_single_shutdown()
    {
        var gate = new DeferredShutdownGate();
        gate.Track(Task.FromCanceled(new CancellationToken(true)));
        var cancellations = 0;

        var result = await gate.WaitForSafeShutdownAsync(() => cancellations++);

        result.OperationException.Should().BeNull();
        cancellations.Should().Be(1);
    }

    [Fact]
    public async Task Cancellation_callback_failure_is_reported_but_does_not_block_shutdown()
    {
        var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("controlled cancellation failure");
        var harness = new RecoveryLifecycleHarness { CancelAction = () => throw failure };
        harness.Track(operation.Task);

        var close = harness.RequestExternalClose();
        close.IsCompleted.Should().BeFalse();
        harness.Shutdowns.Should().Be(0);
        operation.SetResult();
        await close;
        harness.WindowClosed();

        harness.CancellationException.Should().BeSameAs(failure);
        harness.OperationException.Should().BeNull();
        harness.Cancellations.Should().Be(1);
        harness.Shutdowns.Should().Be(1);
        harness.Transitions.Should().Be(0);
    }

    [Fact]
    public async Task External_close_followed_by_window_closed_cancels_operation_exactly_once()
    {
        using var cancellation = new CountingCancellation();
        var tokenCallbacks = 0;
        using var registration = cancellation.Token.Register(() => tokenCallbacks++);
        var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new RecoveryLifecycleHarness { CancelAction = cancellation.Cancel };
        harness.Track(operation.Task);

        var close = harness.RequestExternalClose();
        cancellation.CancelCalls.Should().Be(1);
        tokenCallbacks.Should().Be(1);
        operation.SetResult();
        await close;
        harness.WindowClosed();

        cancellation.CancelCalls.Should().Be(1);
        tokenCallbacks.Should().Be(1);
        harness.Shutdowns.Should().Be(1);
        harness.Transitions.Should().Be(0);
    }

    [Fact]
    public async Task Internal_transition_window_closed_does_not_cancel_operation()
    {
        using var cancellation = new CountingCancellation();
        var tokenCallbacks = 0;
        using var registration = cancellation.Token.Register(() => tokenCallbacks++);
        var startup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new RecoveryLifecycleHarness { CancelAction = cancellation.Cancel };
        var operation = harness.RunPostRestoreAsync(startup.Task);
        harness.Track(operation);

        startup.SetResult();
        await operation;
        harness.WindowClosed();

        cancellation.CancelCalls.Should().Be(0);
        tokenCallbacks.Should().Be(0);
        harness.Shutdowns.Should().Be(0);
        harness.Transitions.Should().Be(1);
        harness.Authorizations.Should().Be(1);
    }

    [Fact]
    public async Task Close_between_restore_tracking_and_workflow_start_cancels_the_same_restore()
    {
        using var source = new CancellationTokenSource();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workflowStarted = false;
        var shutdowns = 0;
        var transitions = 0;
        var authorizations = 0;
        var gate = new DeferredShutdownGate();
        var operation = RunRestoreAsync(source.Token, start.Task);
        gate.Track(operation);

        var close = CloseAsync();
        source.IsCancellationRequested.Should().BeTrue();
        close.IsCompleted.Should().BeFalse();
        start.SetResult();
        await operation;
        await close;

        workflowStarted.Should().BeFalse();
        shutdowns.Should().Be(1);
        transitions.Should().Be(0);
        authorizations.Should().Be(0);

        async Task RunRestoreAsync(CancellationToken cancellationToken, Task startGate)
        {
            await startGate;
            if (cancellationToken.IsCancellationRequested) return;
            workflowStarted = true;
        }

        async Task CloseAsync()
        {
            await gate.WaitForSafeShutdownAsync(source.Cancel);
            shutdowns++;
        }
    }

    [Fact]
    public async Task Close_requested_during_post_restore_startup_wins_over_internal_transition()
    {
        var startup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new RecoveryLifecycleHarness();
        var operation = harness.RunPostRestoreAsync(startup.Task);
        harness.Track(operation);

        var close = harness.RequestExternalClose();
        close.IsCompleted.Should().BeFalse();
        harness.Shutdowns.Should().Be(0);
        harness.Transitions.Should().Be(0);

        startup.SetResult();
        await operation;
        await close;

        harness.Shutdowns.Should().Be(1);
        harness.Transitions.Should().Be(0);
        harness.Authorizations.Should().Be(0);
        harness.Cancellations.Should().Be(1);
    }

    [Fact]
    public async Task Successful_post_restore_startup_transitions_without_application_shutdown()
    {
        var startup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new RecoveryLifecycleHarness();
        var operation = harness.RunPostRestoreAsync(startup.Task);
        harness.Track(operation);

        startup.SetResult();
        await operation;

        harness.Transitions.Should().Be(1);
        harness.Authorizations.Should().Be(1);
        harness.Shutdowns.Should().Be(0);
    }

    [Fact]
    public async Task Idle_external_close_invokes_shutdown_once()
    {
        var harness = new RecoveryLifecycleHarness();
        harness.Track(Task.CompletedTask);

        await harness.RequestExternalClose();

        harness.Shutdowns.Should().Be(1);
        harness.Cancellations.Should().Be(1);
        harness.Transitions.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task External_close_after_cancelled_or_faulted_operation_invokes_shutdown_once(bool faulted)
    {
        var harness = new RecoveryLifecycleHarness();
        var failure = new InvalidOperationException("controlled failure");
        harness.Track(faulted ? Task.FromException(failure) : Task.FromCanceled(new CancellationToken(true)));

        await harness.RequestExternalClose();

        harness.Shutdowns.Should().Be(1);
        harness.Cancellations.Should().Be(1);
        harness.OperationException.Should().Be(faulted ? failure : null);
    }

    [Fact]
    public async Task Repeated_system_and_button_close_cancel_active_operation_exactly_once()
    {
        using var source = new CancellationTokenSource();
        var tokenCallbacks = 0;
        using var registration = source.Token.Register(() => tokenCallbacks++);
        var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new RecoveryLifecycleHarness();
        harness.CancelAction = source.Cancel;
        harness.Track(operation.Task);

        var systemClose = harness.RequestExternalClose();
        var buttonClose = harness.RequestExternalClose();

        buttonClose.Should().BeSameAs(systemClose);
        harness.Cancellations.Should().Be(1);
        tokenCallbacks.Should().Be(1);
        harness.Shutdowns.Should().Be(0);
        operation.SetResult();
        await systemClose;
        harness.Shutdowns.Should().Be(1);
        harness.Transitions.Should().Be(0);
    }

    private sealed class RecoveryLifecycleHarness
    {
        private readonly DeferredShutdownGate gate = new();
        private readonly RecoveryCloseIntent intent = new();
        private Task? closeTask;
        public int Cancellations { get; private set; }
        public int Shutdowns { get; private set; }
        public int Transitions { get; private set; }
        public int Authorizations { get; private set; }
        public Exception? OperationException { get; private set; }
        public Exception? CancellationException { get; private set; }
        public Action? CancelAction { get; set; }

        public void Track(Task operation) => gate.Track(operation);

        public void WindowClosed()
        {
            // Production Closed only marks the window inactive; cancellation belongs to the gate.
        }

        public async Task RunPostRestoreAsync(Task startup)
        {
            await startup;
            if (intent.IsCloseRequested) return;
            if (!intent.TryAuthorizeInternalClose()) return;
            Authorizations++;
            Transitions++;
        }

        public Task RequestExternalClose()
        {
            if (!intent.RequestClose()) return closeTask!;
            closeTask = CloseWhenSafeAsync();
            return closeTask;
        }

        private async Task CloseWhenSafeAsync()
        {
            var result = await gate.WaitForSafeShutdownAsync(() =>
            {
                Cancellations++;
                CancelAction?.Invoke();
            });
            OperationException = result.OperationException;
            CancellationException = result.CancellationException;
            Shutdowns++;
        }
    }

    private sealed class CountingCancellation : IDisposable
    {
        private readonly CancellationTokenSource source = new();
        public int CancelCalls { get; private set; }
        public CancellationToken Token => source.Token;

        public void Cancel()
        {
            CancelCalls++;
            source.Cancel();
        }

        public void Dispose() => source.Dispose();
    }

    private sealed class DedicatedSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = [];
        private readonly Thread thread;
        public int ThreadId { get; private set; }

        public DedicatedSynchronizationContext()
        {
            thread = new Thread(Run) { IsBackground = true };
            thread.Start();
            while (ThreadId == 0) Thread.Yield();
        }

        public override void Post(SendOrPostCallback d, object? state) => queue.Add((d, state));

        public Task RunAsync(Func<Task> action)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(async _ =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception exception) { completion.SetException(exception); }
            }, null);
            return completion.Task;
        }

        private void Run()
        {
            ThreadId = Environment.CurrentManagedThreadId;
            SetSynchronizationContext(this);
            foreach (var item in queue.GetConsumingEnumerable()) item.Callback(item.State);
        }

        public void Dispose()
        {
            queue.CompleteAdding();
            thread.Join();
            queue.Dispose();
        }
    }
}
