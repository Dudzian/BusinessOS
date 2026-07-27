using System.Diagnostics;
using System.Reflection;
using BusinessOS.AppHost;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BusinessOS.IntegrationTests;

[Collection("Environment variables")]
public sealed class ApplicationHostStartupTests
{
    [Fact]
    public async Task Reported_DiagnosticId_matches_ILogger_entry_when_host_is_available()
    {
        using var traceOutput = new StringWriter();
        using var traceListener = new TextWriterTraceListener(traceOutput);
        Trace.Listeners.Add(traceListener);
        var provider = new RecordingLoggerProvider();
        var host = new FakeHost(
            _ => Task.CompletedTask,
            coordinator: new ThrowingCoordinator(new IOException("sensitive persistence failure")),
            loggerProvider: provider);
        var startup = new ApplicationHostStartup(() => host);

        try
        {
            var result = await startup.EnsureHostAndPersistenceReadyAsync(CancellationToken.None);
            Trace.Flush();

            result.Succeeded.Should().BeFalse();
            result.FailureCode.Should().Be(ApplicationStartupFailureCode.UnexpectedFailure);
            result.DiagnosticId.Should().NotBeNullOrWhiteSpace();
            provider.Entries.Should().ContainSingle();
            provider.Entries[0].Message.Should().Contain(result.DiagnosticId!).And.Contain("InitializePersistence");
            provider.Entries[0].Exception.ToString().Should().Contain("sensitive persistence failure");
            result.UserMessage.Should().NotContain("sensitive persistence failure");
            traceOutput.ToString().Should().BeEmpty("the available ILogger must handle the diagnostic without Trace fallback");
        }
        finally { Trace.Listeners.Remove(traceListener); }
    }

    [Fact]
    public async Task Logger_failure_falls_back_to_Trace_with_original_stage_and_DiagnosticId()
    {
        using var output = new StringWriter();
        using var listener = new TextWriterTraceListener(output);
        Trace.Listeners.Add(listener);
        try
        {
            var host = new FakeHost(
                _ => Task.CompletedTask,
                coordinator: new ThrowingCoordinator(new IOException("original technical failure")),
                loggerProvider: new ThrowingLoggerProvider());
            var startup = new ApplicationHostStartup(() => host);

            var result = await startup.EnsureHostAndPersistenceReadyAsync(CancellationToken.None);
            var nextResult = startup.ReportUnexpectedFailure(
                "InitializePersistence",
                "Nie udało się przygotować bazy danych.",
                new IOException("second technical failure"));
            Trace.Flush();
            var log = output.ToString();

            result.Succeeded.Should().BeFalse();
            log.Should().Contain(result.DiagnosticId!).And.Contain("InitializePersistence");
            log.Should().Contain("original technical failure").And.Contain("diagnostic logger failed");
            result.UserMessage.Should().NotContain("original technical failure");
            nextResult.DiagnosticId.Should().NotBe(result.DiagnosticId);
            log.Should().Contain(nextResult.DiagnosticId!).And.Contain("second technical failure");
        }
        finally { Trace.Listeners.Remove(listener); }
    }

    [Fact]
    public async Task Cancellation_during_StartAsync_disposes_and_resets_partial_host()
    {
        using var cancellation = new CancellationTokenSource();
        var firstHost = new FakeHost(token =>
        {
            cancellation.Cancel();
            return Task.FromCanceled(token);
        });
        var secondHost = FakeHost.Successful();
        var hosts = new Queue<IHost>([firstHost, secondHost]);
        var factoryCalls = 0;
        var startup = new ApplicationHostStartup(() => { factoryCalls++; return hosts.Dequeue(); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            startup.EnsureHostAndPersistenceReadyAsync(cancellation.Token));

        firstHost.StartCalls.Should().Be(1);
        firstHost.DisposeAsyncCalls.Should().Be(1);
        startup.Host.Should().BeNull();
        startup.HostStarted.Should().BeFalse();

        var retry = await startup.EnsureHostAndPersistenceReadyAsync(CancellationToken.None);
        retry.Succeeded.Should().BeTrue();
        factoryCalls.Should().Be(2);
        firstHost.StartCalls.Should().Be(1);
        secondHost.StartCalls.Should().Be(1);
        startup.Host.Should().BeSameAs(secondHost);
        startup.HostStarted.Should().BeTrue();
    }

    [Fact]
    public async Task Dispose_failure_does_not_mask_StartAsync_failure()
    {
        var firstHost = new FakeHost(
            _ => Task.FromException(new InvalidOperationException("start failed")),
            disposeException: new IOException("dispose failed"));
        var secondHost = FakeHost.Successful();
        var hosts = new Queue<IHost>([firstHost, secondHost]);
        var factoryCalls = 0;
        var startup = new ApplicationHostStartup(() => { factoryCalls++; return hosts.Dequeue(); });

        var result = await startup.EnsureHostAndPersistenceReadyAsync(CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.FailureCode.Should().Be(ApplicationStartupFailureCode.UnexpectedFailure);
        result.DiagnosticId.Should().NotBeNullOrWhiteSpace();
        startup.Host.Should().BeNull();
        startup.HostStarted.Should().BeFalse();
        firstHost.DisposeAsyncCalls.Should().Be(1);

        (await startup.EnsureHostAndPersistenceReadyAsync(CancellationToken.None)).Succeeded.Should().BeTrue();
        factoryCalls.Should().Be(2);
    }

    [Fact]
    public async Task Reported_DiagnosticId_matches_logged_DiagnosticId_in_Trace_fallback()
    {
        using var output = new StringWriter();
        using var listener = new TextWriterTraceListener(output);
        Trace.Listeners.Add(listener);
        try
        {
            var startup = new ApplicationHostStartup(() => throw new IOException("sensitive technical path"));

            var first = await startup.EnsureHostAndPersistenceReadyAsync(CancellationToken.None);
            var second = await startup.EnsureHostAndPersistenceReadyAsync(CancellationToken.None);
            Trace.Flush();
            var log = output.ToString();

            first.DiagnosticId.Should().NotBeNullOrWhiteSpace();
            log.Should().Contain(first.DiagnosticId!).And.Contain("sensitive technical path").And.Contain("BuildHost");
            first.UserMessage.Should().NotContain("sensitive technical path");
            second.DiagnosticId.Should().NotBe(first.DiagnosticId);
            log.Should().Contain(second.DiagnosticId!);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    [Fact]
    public async Task Invalid_MaxBackups_becomes_controlled_failure_and_retry_does_not_retain_a_partial_host()
    {
        const string variable = "BusinessOS__Persistence__MaxBackups";
        var previous = Environment.GetEnvironmentVariable(variable);
        var factoryCalls = 0;
        Environment.SetEnvironmentVariable(variable, "2147483648");
        try
        {
            var startup = new ApplicationHostStartup(() =>
            {
                factoryCalls++;
                return BusinessOsHost.BuildHost(Assembly.GetExecutingAssembly());
            });

            var first = await startup.EnsureHostAndPersistenceReadyAsync(CancellationToken.None);
            var retry = await startup.EnsureHostAndPersistenceReadyAsync(CancellationToken.None);

            first.Succeeded.Should().BeFalse();
            first.FailureCode.Should().Be(ApplicationStartupFailureCode.UnexpectedFailure);
            first.UserMessage.Should().NotContain("2147483648");
            first.DiagnosticId.Should().NotBeNullOrWhiteSpace();
            retry.Succeeded.Should().BeFalse();
            retry.DiagnosticId.Should().NotBeNullOrWhiteSpace();
            startup.Host.Should().BeNull();
            startup.HostStarted.Should().BeFalse();
            factoryCalls.Should().Be(2);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Fact]
    public async Task Shutdown_stops_disposes_and_resets_a_started_host()
    {
        var host = FakeHost.Successful();
        var startup = new ApplicationHostStartup(() => host);

        (await startup.EnsureHostAndPersistenceReadyAsync(CancellationToken.None)).Succeeded.Should().BeTrue();
        await startup.ShutdownAsync(CancellationToken.None);

        host.StartCalls.Should().Be(1);
        host.StopCalls.Should().Be(1);
        host.DisposeAsyncCalls.Should().Be(1);
        startup.Host.Should().BeNull();
        startup.HostStarted.Should().BeFalse();
    }

    [Fact]
    public async Task Shutdown_is_idempotent()
    {
        var host = FakeHost.Successful();
        var startup = new ApplicationHostStartup(() => host);
        (await startup.EnsureHostAndPersistenceReadyAsync(CancellationToken.None)).Succeeded.Should().BeTrue();

        await Task.WhenAll(
            startup.ShutdownAsync(CancellationToken.None),
            startup.ShutdownAsync(CancellationToken.None),
            startup.ShutdownAsync(CancellationToken.None));

        host.StopCalls.Should().Be(1);
        host.DisposeAsyncCalls.Should().Be(1);
    }

    [Fact]
    public async Task Stop_failure_does_not_prevent_dispose_and_reset()
    {
        var loggerProvider = new RecordingLoggerProvider();
        var host = new FakeHost(
            _ => Task.CompletedTask,
            stopException: new IOException("stop failed"),
            loggerProvider: loggerProvider);
        var startup = new ApplicationHostStartup(() => host);
        (await startup.EnsureHostAndPersistenceReadyAsync(CancellationToken.None)).Succeeded.Should().BeTrue();

        var action = () => startup.ShutdownAsync(CancellationToken.None);

        await action.Should().NotThrowAsync();
        host.StopCalls.Should().Be(1);
        host.DisposeAsyncCalls.Should().Be(1);
        startup.Host.Should().BeNull();
        startup.HostStarted.Should().BeFalse();
        loggerProvider.Entries.Should().ContainSingle(entry =>
            entry.Message.Contains("StopHost", StringComparison.Ordinal) &&
            entry.Exception.Message == "stop failed");
    }

    [Fact]
    public async Task Dispose_failure_does_not_retain_stopped_host()
    {
        var host = new FakeHost(_ => Task.CompletedTask, disposeException: new IOException("dispose failed"));
        var startup = new ApplicationHostStartup(() => host);
        (await startup.EnsureHostAndPersistenceReadyAsync(CancellationToken.None)).Succeeded.Should().BeTrue();

        await startup.ShutdownAsync(CancellationToken.None);
        await startup.ShutdownAsync(CancellationToken.None);

        host.StopCalls.Should().Be(1);
        host.DisposeAsyncCalls.Should().Be(1);
        startup.Host.Should().BeNull();
        startup.HostStarted.Should().BeFalse();
    }

    [Fact]
    public async Task Startup_failure_retry_success_then_shutdown_uses_only_the_successful_host()
    {
        var failedHost = new FakeHost(_ => Task.FromException(new IOException("start failed")));
        var successfulHost = FakeHost.Successful();
        var hosts = new Queue<IHost>([failedHost, successfulHost]);
        var startup = new ApplicationHostStartup(hosts.Dequeue);

        (await startup.EnsureHostAndPersistenceReadyAsync(CancellationToken.None)).Succeeded.Should().BeFalse();
        (await startup.EnsureHostAndPersistenceReadyAsync(CancellationToken.None)).Succeeded.Should().BeTrue();
        await startup.ShutdownAsync(CancellationToken.None);

        failedHost.StartCalls.Should().Be(1);
        failedHost.StopCalls.Should().Be(0);
        failedHost.DisposeAsyncCalls.Should().Be(1);
        successfulHost.StartCalls.Should().Be(1);
        successfulHost.StopCalls.Should().Be(1);
        successfulHost.DisposeAsyncCalls.Should().Be(1);
        startup.Host.Should().BeNull();
        startup.HostStarted.Should().BeFalse();
    }

    [Fact]
    public async Task Shutdown_requested_before_retry_prevents_a_new_host()
    {
        var host = new FakeHost(_ => Task.CompletedTask, coordinator: new ControlledFailureCoordinator());
        var factoryCalls = 0;
        var startup = new ApplicationHostStartup(() => { factoryCalls++; return host; });

        (await startup.EnsureHostAndPersistenceReadyAsync(CancellationToken.None)).Succeeded.Should().BeFalse();
        await startup.ShutdownAsync(CancellationToken.None);
        var retry = await startup.EnsureHostAndPersistenceReadyAsync(CancellationToken.None);

        retry.Status.Should().Be(ApplicationStartupStatus.Cancelled);
        factoryCalls.Should().Be(1);
        startup.Host.Should().BeNull();
        startup.HostStarted.Should().BeFalse();
    }

    [Fact]
    public async Task Shutdown_racing_successful_retry_stops_the_resulting_host_exactly_once()
    {
        var retryEntered = NewCompletionSource();
        var releaseRetry = NewCompletionSource();
        var coordinator = new SequencedCoordinator(
            _ => Task.FromResult(ControlledFailure()),
            async _ => { retryEntered.SetResult(); await releaseRetry.Task; return ApplicationStartupResult.Success(false, false, null); });
        var host = new FakeHost(_ => Task.CompletedTask, coordinator: coordinator);
        var factoryCalls = 0;
        var startup = new ApplicationHostStartup(() => { factoryCalls++; return host; });
        (await startup.EnsureHostAndPersistenceReadyAsync(CancellationToken.None)).Succeeded.Should().BeFalse();

        var retry = startup.EnsureHostAndPersistenceReadyAsync(CancellationToken.None);
        await retryEntered.Task;
        var shutdown = startup.ShutdownAsync(CancellationToken.None);
        releaseRetry.SetResult();
        await Task.WhenAll(retry, shutdown);

        var retryResult = await retry;
        retryResult.Status.Should().Be(ApplicationStartupStatus.Cancelled);
        retryResult.Succeeded.Should().BeFalse();
        factoryCalls.Should().Be(1);
        host.StopCalls.Should().Be(1);
        host.DisposeAsyncCalls.Should().Be(1);
        startup.Host.Should().BeNull();
        startup.HostStarted.Should().BeFalse();
    }

    [Fact]
    public async Task Shutdown_requested_while_coordinator_returns_success_never_exposes_success()
    {
        var coordinatorEntered = NewCompletionSource();
        var releaseCoordinator = NewCompletionSource();
        var coordinatorReturnedSuccess = false;
        var coordinator = new SequencedCoordinator(async _ =>
        {
            coordinatorEntered.SetResult();
            await releaseCoordinator.Task;
            coordinatorReturnedSuccess = true;
            return ApplicationStartupResult.Success(false, false, null);
        });
        var host = new FakeHost(_ => Task.CompletedTask, coordinator: coordinator);
        var startup = new ApplicationHostStartup(() => host);

        var initialization = startup.EnsureHostAndPersistenceReadyAsync(CancellationToken.None);
        await coordinatorEntered.Task;
        var shutdown = startup.ShutdownAsync(CancellationToken.None);
        releaseCoordinator.SetResult();
        await Task.WhenAll(initialization, shutdown);

        coordinatorReturnedSuccess.Should().BeTrue();
        (await initialization).Status.Should().Be(ApplicationStartupStatus.Cancelled);
        host.StopCalls.Should().Be(1);
        host.DisposeAsyncCalls.Should().Be(1);
        startup.Host.Should().BeNull();
        startup.HostStarted.Should().BeFalse();
    }

    [Fact]
    public async Task Shutdown_requested_while_coordinator_returns_failure_exposes_cancelled()
    {
        var coordinatorEntered = NewCompletionSource();
        var releaseCoordinator = NewCompletionSource();
        var coordinatorReturnedFailure = false;
        var coordinator = new SequencedCoordinator(async _ =>
        {
            coordinatorEntered.SetResult();
            await releaseCoordinator.Task;
            coordinatorReturnedFailure = true;
            return ControlledFailure();
        });
        var host = new FakeHost(_ => Task.CompletedTask, coordinator: coordinator);
        var startup = new ApplicationHostStartup(() => host);

        var initialization = startup.EnsureHostAndPersistenceReadyAsync(CancellationToken.None);
        await coordinatorEntered.Task;
        var shutdown = startup.ShutdownAsync(CancellationToken.None);
        releaseCoordinator.SetResult();
        await Task.WhenAll(initialization, shutdown);

        coordinatorReturnedFailure.Should().BeTrue();
        (await initialization).Status.Should().Be(ApplicationStartupStatus.Cancelled);
        host.StopCalls.Should().Be(1);
        host.DisposeAsyncCalls.Should().Be(1);
        startup.Host.Should().BeNull();
        startup.HostStarted.Should().BeFalse();
    }

    [Fact]
    public async Task No_host_can_start_after_shutdown_requested()
    {
        var factoryCalls = 0;
        var startup = new ApplicationHostStartup(() => { factoryCalls++; return FakeHost.Successful(); });
        await startup.ShutdownAsync(CancellationToken.None);

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => startup.EnsureHostAndPersistenceReadyAsync(CancellationToken.None)));

        results.Should().OnlyContain(result => result.Status == ApplicationStartupStatus.Cancelled);
        factoryCalls.Should().Be(0);
        startup.Host.Should().BeNull();
        startup.HostStarted.Should().BeFalse();
    }

    [Fact]
    public async Task Concurrent_shutdown_calls_and_retry_remain_idempotent()
    {
        var retryEntered = NewCompletionSource();
        var releaseRetry = NewCompletionSource();
        var coordinator = new SequencedCoordinator(
            _ => Task.FromResult(ControlledFailure()),
            async _ => { retryEntered.SetResult(); await releaseRetry.Task; return ApplicationStartupResult.Success(false, false, null); });
        var host = new FakeHost(_ => Task.CompletedTask, coordinator: coordinator);
        var factoryCalls = 0;
        var startup = new ApplicationHostStartup(() => { factoryCalls++; return host; });
        (await startup.EnsureHostAndPersistenceReadyAsync(CancellationToken.None)).Succeeded.Should().BeFalse();

        var retry = startup.EnsureHostAndPersistenceReadyAsync(CancellationToken.None);
        await retryEntered.Task;
        var shutdowns = Enumerable.Range(0, 4).Select(_ => startup.ShutdownAsync(CancellationToken.None)).ToArray();
        releaseRetry.SetResult();
        await Task.WhenAll(shutdowns.Append(retry));

        factoryCalls.Should().Be(1);
        host.StopCalls.Should().Be(1);
        host.DisposeAsyncCalls.Should().Be(1);
        (await startup.EnsureHostAndPersistenceReadyAsync(CancellationToken.None)).Status.Should().Be(ApplicationStartupStatus.Cancelled);
        factoryCalls.Should().Be(1);
    }

    private static TaskCompletionSource NewCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static ApplicationStartupResult ControlledFailure() =>
        ApplicationStartupResult.Failure(ApplicationStartupFailureCode.MigrationFailed, "controlled failure", "test-diagnostic");

    private sealed class FakeHost : IHost, IAsyncDisposable
    {
        private readonly Func<CancellationToken, Task> start;
        private readonly Exception? stopException;
        private readonly Exception? disposeException;

        public FakeHost(
            Func<CancellationToken, Task> start,
            Exception? stopException = null,
            Exception? disposeException = null,
            IApplicationStartupCoordinator? coordinator = null,
            ILoggerProvider? loggerProvider = null)
        {
            this.start = start;
            this.stopException = stopException;
            this.disposeException = disposeException;
            var services = new ServiceCollection();
            services.AddLogging(builder =>
            {
                builder.ClearProviders();
                if (loggerProvider is not null) builder.AddProvider(loggerProvider);
            });
            services.AddSingleton<IApplicationStartupCoordinator>(coordinator ?? new SuccessfulCoordinator());
            Services = services.BuildServiceProvider();
        }

        public IServiceProvider Services { get; }
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public int DisposeAsyncCalls { get; private set; }

        public static FakeHost Successful() => new(_ => Task.CompletedTask);

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCalls++;
            return start(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCalls++;
            return stopException is null ? Task.CompletedTask : Task.FromException(stopException);
        }
        public void Dispose() { }
        public ValueTask DisposeAsync()
        {
            DisposeAsyncCalls++;
            if (disposeException is not null) return ValueTask.FromException(disposeException);
            if (Services is IDisposable disposable) disposable.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SuccessfulCoordinator : IApplicationStartupCoordinator
    {
        public Task<ApplicationStartupResult> InitializeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ApplicationStartupResult.Success(false, false, null));
    }

    private sealed class ControlledFailureCoordinator : IApplicationStartupCoordinator
    {
        public Task<ApplicationStartupResult> InitializeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ControlledFailure());
    }

    private sealed class SequencedCoordinator(params Func<CancellationToken, Task<ApplicationStartupResult>>[] operations)
        : IApplicationStartupCoordinator
    {
        private readonly Queue<Func<CancellationToken, Task<ApplicationStartupResult>>> operations = new(operations);

        public Task<ApplicationStartupResult> InitializeAsync(CancellationToken cancellationToken) =>
            operations.Dequeue()(cancellationToken);
    }

    private sealed class ThrowingCoordinator(Exception exception) : IApplicationStartupCoordinator
    {
        public Task<ApplicationStartupResult> InitializeAsync(CancellationToken cancellationToken) =>
            Task.FromException<ApplicationStartupResult>(exception);
    }

    private sealed record LogEntry(string Message, Exception Exception);

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public List<LogEntry> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Entries);
        public void Dispose() { }
        private sealed class RecordingLogger(List<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Add(new(formatter(state, exception), exception!));
        }
    }

    private sealed class ThrowingLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ThrowingLogger();
        public void Dispose() { }
        private sealed class ThrowingLogger : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                throw new InvalidOperationException("logger failed");
        }
    }
}

[CollectionDefinition("Environment variables", DisableParallelization = true)]
public sealed class EnvironmentVariablesCollection;
