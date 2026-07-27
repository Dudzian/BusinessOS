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

    private sealed class FakeHost : IHost, IAsyncDisposable
    {
        private readonly Func<CancellationToken, Task> start;
        private readonly Exception? disposeException;

        public FakeHost(
            Func<CancellationToken, Task> start,
            Exception? disposeException = null,
            IApplicationStartupCoordinator? coordinator = null,
            ILoggerProvider? loggerProvider = null)
        {
            this.start = start;
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
        public int DisposeAsyncCalls { get; private set; }

        public static FakeHost Successful() => new(_ => Task.CompletedTask);

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCalls++;
            return start(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
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
