using BusinessOS.AppHost;
using BusinessOS.Modules.Companies.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BusinessOS.IntegrationTests;

public sealed class ApplicationStartupCoordinatorFailureTests
{
    [Fact]
    public async Task Backup_failure_result_prevents_migration()
    {
        var initializer = new FakeDatabaseInitializer();
        var result = await CreateCoordinator(backup: new FakeBackupService(CompaniesBackupResult.Failure(CompaniesBackupFailureCode.BackupFailed)), initializer: initializer)
            .InitializeAsync(CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.FailureCode.Should().Be(ApplicationStartupFailureCode.BackupFailed);
        result.DiagnosticId.Should().NotBeNullOrWhiteSpace();
        initializer.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Backup_integrity_failure_prevents_migration()
    {
        var initializer = new FakeDatabaseInitializer();
        var result = await CreateCoordinator(backup: new FakeBackupService(CompaniesBackupResult.Failure(CompaniesBackupFailureCode.IntegrityCheckFailed)), initializer: initializer)
            .InitializeAsync(CancellationToken.None);

        result.FailureCode.Should().Be(ApplicationStartupFailureCode.BackupIntegrityCheckFailed);
        initializer.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Thrown_backup_exception_becomes_safe_BackupFailed_result()
    {
        var initializer = new FakeDatabaseInitializer();
        var result = await CreateCoordinator(backup: new FakeBackupService(new IOException("sensitive technical path")), initializer: initializer)
            .InitializeAsync(CancellationToken.None);

        result.FailureCode.Should().Be(ApplicationStartupFailureCode.BackupFailed);
        result.DiagnosticId.Should().NotBeNullOrWhiteSpace();
        result.UserMessage.Should().NotContain("sensitive technical path");
        result.ToString().Should().NotContain("sensitive technical path");
        initializer.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Migration_failure_preserves_valid_backup()
    {
        var path = Path.GetTempFileName();
        try
        {
            var result = await CreateCoordinator(
                backup: new FakeBackupService(CompaniesBackupResult.Success(path)),
                initializer: new FakeDatabaseInitializer(new IOException("migration failed")))
                .InitializeAsync(CancellationToken.None);

            result.FailureCode.Should().Be(ApplicationStartupFailureCode.MigrationFailed);
            result.BackupCreated.Should().BeTrue();
            result.BackupPath.Should().Be(path);
            result.DiagnosticId.Should().NotBeNullOrWhiteSpace();
            File.Exists(path).Should().BeTrue();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Cancellation_from_backup_service_is_propagated()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var initializer = new FakeDatabaseInitializer();
        var coordinator = CreateCoordinator(backup: new FakeBackupService(new OperationCanceledException(cancellation.Token)), initializer: initializer);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.InitializeAsync(cancellation.Token));
        initializer.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Cancellation_from_initializer_is_propagated()
    {
        using var cancellation = new CancellationTokenSource();
        var initializer = new FakeDatabaseInitializer(() =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        var coordinator = CreateCoordinator(inspector: new FakeMigrationInspector(new CompaniesMigrationState(false, ["pending"])), initializer: initializer);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.InitializeAsync(cancellation.Token));
    }

    [Fact]
    public async Task Cancellation_while_waiting_for_initialization_lock_is_propagated()
    {
        var inspector = new BlockingMigrationInspector();
        var coordinator = CreateCoordinator(inspector: inspector);
        var first = coordinator.InitializeAsync(CancellationToken.None);
        await inspector.Entered.Task;
        using var cancellation = new CancellationTokenSource();
        var second = coordinator.InitializeAsync(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        inspector.Release.TrySetResult();
        (await first).Succeeded.Should().BeTrue();
        inspector.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_initialization_is_serialized_within_the_process()
    {
        var inspector = new BlockingMigrationInspector();
        var coordinator = CreateCoordinator(inspector: inspector);
        var first = coordinator.InitializeAsync(CancellationToken.None);
        await inspector.Entered.Task;
        var second = coordinator.InitializeAsync(CancellationToken.None);
        inspector.CallCount.Should().Be(1);
        inspector.Release.TrySetResult();

        var results = await Task.WhenAll(first, second);
        results.Should().OnlyContain(result => result.Succeeded);
        inspector.MaxConcurrency.Should().Be(1);
        inspector.CallCount.Should().Be(2);
    }

    private static ApplicationStartupCoordinator CreateCoordinator(
        ICompaniesMigrationInspector? inspector = null,
        ICompaniesDatabaseBackupService? backup = null,
        ICompaniesDatabaseInitializer? initializer = null) => new(
            inspector ?? new FakeMigrationInspector(new CompaniesMigrationState(true, ["pending"])),
            backup ?? new FakeBackupService(CompaniesBackupResult.Success("backup.db")),
            initializer ?? new FakeDatabaseInitializer(),
            NullLogger<ApplicationStartupCoordinator>.Instance);

    private sealed class FakeMigrationInspector(CompaniesMigrationState state) : ICompaniesMigrationInspector
    {
        public int CallCount { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public Task<CompaniesMigrationState> InspectAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            LastToken = cancellationToken;
            return Task.FromResult(state);
        }
    }

    private sealed class FakeBackupService : ICompaniesDatabaseBackupService
    {
        private readonly CompaniesBackupResult? result;
        private readonly Exception? exception;
        public int CallCount { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public FakeBackupService(CompaniesBackupResult result) => this.result = result;
        public FakeBackupService(Exception exception) => this.exception = exception;
        public Task<CompaniesBackupResult> CreateBackupAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            LastToken = cancellationToken;
            return exception is null ? Task.FromResult(result!) : Task.FromException<CompaniesBackupResult>(exception);
        }
    }

    private sealed class FakeDatabaseInitializer : ICompaniesDatabaseInitializer
    {
        private readonly Action? action;
        public int CallCount { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public FakeDatabaseInitializer() { }
        public FakeDatabaseInitializer(Exception exception) : this(() => throw exception) { }
        public FakeDatabaseInitializer(Action action) => this.action = action;
        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            LastToken = cancellationToken;
            action?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingMigrationInspector : ICompaniesMigrationInspector
    {
        private int concurrency;
        public int CallCount { get; private set; }
        public int MaxConcurrency { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<CompaniesMigrationState> InspectAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            MaxConcurrency = Math.Max(MaxConcurrency, Interlocked.Increment(ref concurrency));
            try
            {
                if (CallCount == 1)
                {
                    Entered.TrySetResult();
                    await Release.Task.WaitAsync(cancellationToken);
                }
                return new CompaniesMigrationState(true, []);
            }
            finally { Interlocked.Decrement(ref concurrency); }
        }
    }
}
