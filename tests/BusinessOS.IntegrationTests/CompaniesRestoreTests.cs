using BusinessOS.Modules.Companies.Infrastructure.Persistence;
using BusinessOS.Modules.Companies.Domain;
using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.BuildingBlocks.Domain.Primitives;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BusinessOS.IntegrationTests;

public sealed class CompaniesRestoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "businessos-restore-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(root, "data", "companies.db");
    private string BackupDirectory => Path.Combine(root, "backups");

    [Fact]
    public async Task Catalog_filters_sorts_and_rejects_invalid_identifiers()
    {
        using var provider = Services(); await CreateDatabaseAsync("current");
        var backup = provider.GetRequiredService<ICompaniesDatabaseBackupService>();
        var first = await backup.CreateBackupAsync(default); await backup.CreateBackupAsync(default);
        await File.WriteAllTextAsync(Path.Combine(BackupDirectory, "foreign.db"), "foreign");
        Directory.CreateDirectory(Path.Combine(BackupDirectory, CompaniesBackupFileName.Create(DateTimeOffset.UtcNow, Guid.NewGuid())));
        var catalog = provider.GetRequiredService<ICompaniesBackupCatalog>(); var listed = (await catalog.ListAsync(default)).Backups;
        listed.Should().HaveCount(2).And.OnlyContain(x => x.ValidationStatus == CompaniesBackupValidationStatus.Valid && !Path.IsPathRooted(x.BackupId));
        listed[0].CreatedAtUtc.Should().BeOnOrAfter(listed[1].CreatedAtUtc);
        (await catalog.ValidateAsync("../" + Path.GetFileName(first.BackupPath), default)).FailureCode.Should().Be(CompaniesBackupValidationFailureCode.InvalidBackupId);
        (await catalog.ValidateAsync(CompaniesBackupFileName.Create(DateTimeOffset.UtcNow, Guid.NewGuid()), default)).FailureCode.Should().Be(CompaniesBackupValidationFailureCode.BackupNotFound);
    }

    [Fact]
    public async Task Validation_rejects_database_without_companies_history()
    {
        using var provider = Services(); Directory.CreateDirectory(BackupDirectory);
        var name = CompaniesBackupFileName.Create(DateTimeOffset.Parse("2026-01-01T00:00:00Z"), Guid.NewGuid());
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(BackupDirectory, name)};Pooling=False"))
        { await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = "CREATE TABLE foreign_table(value TEXT);"; await command.ExecuteNonQueryAsync(); }
        (await provider.GetRequiredService<ICompaniesBackupCatalog>().ValidateAsync(name, default)).FailureCode.Should().Be(CompaniesBackupValidationFailureCode.NotCompaniesDatabase);
    }

    [Fact]
    public async Task Validation_rejects_unknown_newer_migration()
    {
        using var provider = Services(); var name = await CreateBackupDatabaseAsync("newer", ["20260725183029_InitialCompaniesPersistence", "20990101000000_Future"]);
        (await provider.GetRequiredService<ICompaniesBackupCatalog>().ValidateAsync(name, default)).FailureCode.Should().Be(CompaniesBackupValidationFailureCode.IncompatibleNewerSchema);
    }

    [Fact]
    public async Task Validation_accepts_empty_known_migration_prefix()
    {
        using var provider = Services(); var name = await CreateBackupDatabaseAsync("older", []);
        (await provider.GetRequiredService<ICompaniesBackupCatalog>().ValidateAsync(name, default)).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Validation_rejects_duplicate_migration_ids()
    {
        using var provider = Services(); var migration = "20260725183029_InitialCompaniesPersistence";
        var name = await CreateBackupDatabaseAsync("duplicate", [migration, migration]);
        (await provider.GetRequiredService<ICompaniesBackupCatalog>().ValidateAsync(name, default)).FailureCode.Should().Be(CompaniesBackupValidationFailureCode.IncompatibleNewerSchema);
    }

    [Fact]
    public async Task Validation_returns_backup_not_found()
    {
        using var provider = Services(); var name = CompaniesBackupFileName.Create(DateTimeOffset.UtcNow, Guid.NewGuid());
        (await provider.GetRequiredService<ICompaniesBackupCatalog>().ValidateAsync(name, default)).FailureCode.Should().Be(CompaniesBackupValidationFailureCode.BackupNotFound);
    }

    [Fact]
    public async Task Validation_rejects_reparse_point()
    {
        using var provider = Services(); Directory.CreateDirectory(BackupDirectory);
        var target = Path.Combine(root, "outside.db"); await File.WriteAllTextAsync(target, "target");
        var name = CompaniesBackupFileName.Create(DateTimeOffset.UtcNow, Guid.NewGuid()); File.CreateSymbolicLink(Path.Combine(BackupDirectory, name), target);
        (await provider.GetRequiredService<ICompaniesBackupCatalog>().ValidateAsync(name, default)).FailureCode.Should().Be(CompaniesBackupValidationFailureCode.ReparsePointRejected);
    }

    [Fact]
    public async Task Catalog_marks_empty_backup_invalid()
    {
        using var provider = Services(); Directory.CreateDirectory(BackupDirectory);
        var name = CompaniesBackupFileName.Create(DateTimeOffset.Parse("2026-03-01T00:00:00Z"), Guid.NewGuid()); File.Create(Path.Combine(BackupDirectory, name)).Dispose();
        var descriptor = (await provider.GetRequiredService<ICompaniesBackupCatalog>().ListAsync(default)).Backups.Single();
        descriptor.ValidationStatus.Should().Be(CompaniesBackupValidationStatus.Invalid); descriptor.FailureCode.Should().Be(CompaniesBackupValidationFailureCode.EmptyBackup);
    }

    [Fact]
    public async Task Catalog_marks_corrupt_sqlite_invalid()
    {
        using var provider = Services(); Directory.CreateDirectory(BackupDirectory);
        var name = CompaniesBackupFileName.Create(DateTimeOffset.Parse("2026-03-02T00:00:00Z"), Guid.NewGuid()); await File.WriteAllTextAsync(Path.Combine(BackupDirectory, name), "not sqlite");
        var descriptor = (await provider.GetRequiredService<ICompaniesBackupCatalog>().ListAsync(default)).Backups.Single();
        descriptor.ValidationStatus.Should().Be(CompaniesBackupValidationStatus.Invalid); descriptor.FailureCode.Should().BeOneOf(CompaniesBackupValidationFailureCode.BackupOpenFailed, CompaniesBackupValidationFailureCode.IntegrityCheckFailed);
    }

    [Fact]
    public async Task Replace_failure_before_mutation_preserves_live_database()
    {
        var operations = new FaultOperations(FaultMode.ThrowBeforeReplace);
        using var provider = Services(operations); await CreateDatabaseAsync("selected");
        var selected = await provider.GetRequiredService<ICompaniesDatabaseBackupService>().CreateBackupAsync(default); await SetMarkerAsync("current");
        var result = await provider.GetRequiredService<ICompaniesDatabaseRestoreService>().RestoreAsync(Path.GetFileName(selected.BackupPath!), default);
        result.FailureCode.Should().Be(CompaniesRestoreFailureCode.DatabaseReplacementFailed); result.DatabaseReplaced.Should().BeFalse();
        result.RollbackAttempted.Should().BeFalse(); (await MarkerAsync(DatabasePath)).Should().Be("current"); File.Exists(result.SafetyBackupPath!).Should().BeTrue();
    }

    [Fact]
    public async Task Replace_failure_after_rollback_creation_restores_previous_database()
    {
        var operations = new FaultOperations(FaultMode.ThrowAfterRollback);
        using var provider = Services(operations); await CreateDatabaseAsync("selected");
        var selected = await provider.GetRequiredService<ICompaniesDatabaseBackupService>().CreateBackupAsync(default); await SetMarkerAsync("current");
        var result = await provider.GetRequiredService<ICompaniesDatabaseRestoreService>().RestoreAsync(Path.GetFileName(selected.BackupPath!), default);
        result.FailureCode.Should().Be(CompaniesRestoreFailureCode.DatabaseReplacementFailed); result.RollbackAttempted.Should().BeTrue();
        result.RollbackSucceeded.Should().BeTrue(); (await MarkerAsync(DatabasePath)).Should().Be("current");
    }

    [Fact]
    public async Task Replace_throwing_after_successful_install_reconciles_valid_live_database()
    {
        var operations = new FaultOperations(FaultMode.ThrowAfterInstall);
        using var provider = Services(operations); await CreateDatabaseAsync("selected");
        var selected = await provider.GetRequiredService<ICompaniesDatabaseBackupService>().CreateBackupAsync(default); await SetMarkerAsync("current");
        var result = await provider.GetRequiredService<ICompaniesDatabaseRestoreService>().RestoreAsync(Path.GetFileName(selected.BackupPath!), default);
        result.Succeeded.Should().BeTrue(); result.DatabaseReplaced.Should().BeTrue(); (await MarkerAsync(DatabasePath)).Should().Be("selected");
    }

    [Fact]
    public async Task Move_throwing_after_successful_install_reconciles_valid_live_database()
    {
        var operations = new FaultOperations(FaultMode.ThrowAfterMoveInstall);
        using var provider = Services(operations); await CreateDatabaseAsync("selected");
        var selected = await provider.GetRequiredService<ICompaniesDatabaseBackupService>().CreateBackupAsync(default); File.Delete(DatabasePath);
        var result = await provider.GetRequiredService<ICompaniesDatabaseRestoreService>().RestoreAsync(Path.GetFileName(selected.BackupPath!), default);
        result.Succeeded.Should().BeTrue(); result.CurrentDatabaseExisted.Should().BeFalse(); result.DatabaseReplaced.Should().BeTrue();
        (await MarkerAsync(DatabasePath)).Should().Be("selected");
    }

    [Fact]
    public async Task Checkpoint_failure_preserves_live_database_and_safety_backup()
    {
        var operations = new FaultOperations(FaultMode.None); var maintenance = new TestMaintenance(new(false, false, 2, 1));
        using var provider = Services(operations, maintenance); await CreateDatabaseAsync("selected");
        var selected = await provider.GetRequiredService<ICompaniesDatabaseBackupService>().CreateBackupAsync(default); await SetMarkerAsync("current");
        var result = await provider.GetRequiredService<ICompaniesDatabaseRestoreService>().RestoreAsync(Path.GetFileName(selected.BackupPath!), default);
        result.FailureCode.Should().Be(CompaniesRestoreFailureCode.DatabaseCheckpointFailed); operations.ReplaceCalls.Should().Be(0); operations.MoveCalls.Should().Be(0);
        (await MarkerAsync(DatabasePath)).Should().Be("current"); File.Exists(result.SafetyBackupPath!).Should().BeTrue();
    }

    [Fact]
    public async Task Busy_WAL_checkpoint_does_not_delete_sidecars_or_invoke_replace()
    {
        var operations = new FaultOperations(FaultMode.None); var maintenance = new TestMaintenance(new(false, true, 2, 0), createSidecars: true);
        using var provider = Services(operations, maintenance); await CreateDatabaseAsync("selected");
        var selected = await provider.GetRequiredService<ICompaniesDatabaseBackupService>().CreateBackupAsync(default); await SetMarkerAsync("current");
        var result = await provider.GetRequiredService<ICompaniesDatabaseRestoreService>().RestoreAsync(Path.GetFileName(selected.BackupPath!), default);
        result.FailureCode.Should().Be(CompaniesRestoreFailureCode.DatabaseNotQuiescent); operations.ReplaceCalls.Should().Be(0);
        File.Exists(DatabasePath + "-wal").Should().BeTrue(); File.Exists(DatabasePath + "-shm").Should().BeTrue();
    }

    [Fact]
    public async Task Replace_failure_after_real_WAL_checkpoint_preserves_latest_committed_data()
    {
        var operations = new FaultOperations(FaultMode.ThrowBeforeReplace);
        using var setupProvider = Services(); await CreateDatabaseAsync("selected");
        var selected = await setupProvider.GetRequiredService<ICompaniesDatabaseBackupService>().CreateBackupAsync(default);
        var keeper = new SqliteConnection($"Data Source={DatabasePath};Pooling=False"); await keeper.OpenAsync(); await using var command = keeper.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; UPDATE marker SET value='latest-committed';"; await command.ExecuteNonQueryAsync();
        var maintenance = new RecordingRealMaintenance(keeper); using var provider = Services(operations, maintenance);
        var result = await provider.GetRequiredService<ICompaniesDatabaseRestoreService>().RestoreAsync(Path.GetFileName(selected.BackupPath!), default);
        maintenance.WalExistedAtEntry.Should().BeTrue(); maintenance.WalLengthAtEntry.Should().BeGreaterThan(0); maintenance.Result!.Succeeded.Should().BeTrue();
        maintenance.Result.Busy.Should().BeFalse(); operations.ReplaceCalls.Should().Be(1);
        result.FailureCode.Should().Be(CompaniesRestoreFailureCode.DatabaseReplacementFailed); (await MarkerAsync(DatabasePath)).Should().Be("latest-committed");
        await using var check = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly;Pooling=False"); await check.OpenAsync(); await using var checkCommand = check.CreateCommand(); checkCommand.CommandText = "PRAGMA quick_check;"; (await checkCommand.ExecuteScalarAsync()).Should().Be("ok");
    }

    [Fact]
    public async Task Post_validation_failure_without_previous_database_removes_invalid_install()
    {
        var operations = new FaultOperations(FaultMode.CorruptAfterMove);
        using var provider = Services(operations); await CreateDatabaseAsync("selected");
        var selected = await provider.GetRequiredService<ICompaniesDatabaseBackupService>().CreateBackupAsync(default); File.Delete(DatabasePath);
        var result = await provider.GetRequiredService<ICompaniesDatabaseRestoreService>().RestoreAsync(Path.GetFileName(selected.BackupPath!), default);
        result.FailureCode.Should().Be(CompaniesRestoreFailureCode.PostRestoreValidationFailed); result.DatabaseReplaced.Should().BeTrue();
        File.Exists(DatabasePath).Should().BeFalse(); File.Exists(selected.BackupPath!).Should().BeTrue();
    }

    [Fact]
    public async Task Post_validation_failure_rolls_back_existing_database()
    {
        var operations = new FaultOperations(FaultMode.CorruptAfterReplace);
        using var provider = Services(operations); await CreateDatabaseAsync("selected");
        var selected = await provider.GetRequiredService<ICompaniesDatabaseBackupService>().CreateBackupAsync(default); await SetMarkerAsync("current");
        var result = await provider.GetRequiredService<ICompaniesDatabaseRestoreService>().RestoreAsync(Path.GetFileName(selected.BackupPath!), default);
        result.FailureCode.Should().Be(CompaniesRestoreFailureCode.PostRestoreValidationFailed); result.DatabaseReplaced.Should().BeTrue();
        result.RollbackAttempted.Should().BeTrue(); result.RollbackSucceeded.Should().BeTrue(); (await MarkerAsync(DatabasePath)).Should().Be("current");
    }

    [Fact]
    public async Task Invalid_install_cleanup_failure_returns_controlled_failure()
    {
        var operations = new FaultOperations(FaultMode.CorruptMoveAndFailCleanup);
        using var provider = Services(operations); await CreateDatabaseAsync("selected");
        var selected = await provider.GetRequiredService<ICompaniesDatabaseBackupService>().CreateBackupAsync(default); File.Delete(DatabasePath);
        var result = await provider.GetRequiredService<ICompaniesDatabaseRestoreService>().RestoreAsync(Path.GetFileName(selected.BackupPath!), default);
        result.FailureCode.Should().Be(CompaniesRestoreFailureCode.FailedInstallCleanupFailed); result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Rollback_failure_returns_RollbackFailed_and_preserves_safety_backup()
    {
        var operations = new FaultOperations(FaultMode.CorruptAfterReplaceAndFailRollback);
        using var provider = Services(operations); await CreateDatabaseAsync("selected");
        var selected = await provider.GetRequiredService<ICompaniesDatabaseBackupService>().CreateBackupAsync(default); await SetMarkerAsync("current");
        var result = await provider.GetRequiredService<ICompaniesDatabaseRestoreService>().RestoreAsync(Path.GetFileName(selected.BackupPath!), default);
        result.FailureCode.Should().Be(CompaniesRestoreFailureCode.RollbackFailed); result.RollbackAttempted.Should().BeTrue(); result.RollbackSucceeded.Should().BeFalse();
        File.Exists(result.SafetyBackupPath!).Should().BeTrue();
    }

    [Fact]
    public async Task Rollback_cleanup_failure_reports_required_cleanup_after_successful_rollback()
    {
        var operations = new FaultOperations(FaultMode.CorruptAfterReplaceAndFailCleanup);
        using var provider = Services(operations); await CreateDatabaseAsync("selected");
        var selected = await provider.GetRequiredService<ICompaniesDatabaseBackupService>().CreateBackupAsync(default); await SetMarkerAsync("current");
        var result = await provider.GetRequiredService<ICompaniesDatabaseRestoreService>().RestoreAsync(Path.GetFileName(selected.BackupPath!), default);
        result.FailureCode.Should().Be(CompaniesRestoreFailureCode.RequiredCleanupFailed); result.RollbackAttempted.Should().BeTrue(); result.RollbackSucceeded.Should().BeTrue();
        (await MarkerAsync(DatabasePath)).Should().Be("current"); File.Exists(result.SafetyBackupPath!).Should().BeTrue(); File.Exists(selected.BackupPath!).Should().BeTrue();
    }

    [Fact]
    public async Task Sidecar_cleanup_failure_does_not_invoke_replace()
    {
        var operations = new FaultOperations(FaultMode.ThrowDeletingWal);
        using var provider = Services(operations); await CreateDatabaseAsync("selected");
        var selected = await provider.GetRequiredService<ICompaniesDatabaseBackupService>().CreateBackupAsync(default); await SetMarkerAsync("current");
        await File.WriteAllTextAsync(DatabasePath + "-wal", "old");
        var result = await provider.GetRequiredService<ICompaniesDatabaseRestoreService>().RestoreAsync(Path.GetFileName(selected.BackupPath!), default);
        result.FailureCode.Should().Be(CompaniesRestoreFailureCode.DatabaseSidecarCleanupFailed); operations.ReplaceCalls.Should().Be(0);
        (await MarkerAsync(DatabasePath)).Should().Be("current"); File.Exists(result.SafetyBackupPath!).Should().BeTrue();
    }

    [Fact]
    public async Task Restore_existing_database_creates_safety_backup_and_restores_selected_data()
    {
        using var provider = Services(); await CreateDatabaseAsync("selected");
        var selected = await provider.GetRequiredService<ICompaniesDatabaseBackupService>().CreateBackupAsync(default); await SetMarkerAsync("current");
        var result = await provider.GetRequiredService<ICompaniesDatabaseRestoreService>().RestoreAsync(Path.GetFileName(selected.BackupPath!), default);
        result.Succeeded.Should().BeTrue(); result.CurrentDatabaseExisted.Should().BeTrue(); result.SafetyBackupCreated.Should().BeTrue();
        result.DatabaseReplaced.Should().BeTrue(); result.RollbackAttempted.Should().BeFalse();
        (await MarkerAsync(DatabasePath)).Should().Be("selected"); (await MarkerAsync(result.SafetyBackupPath!)).Should().Be("current");
        File.Exists(selected.BackupPath!).Should().BeTrue(); File.Exists(DatabasePath + "-wal").Should().BeFalse(); File.Exists(DatabasePath + "-shm").Should().BeFalse();
    }

    [Fact]
    public async Task Restore_creates_database_when_live_database_is_missing()
    {
        using var provider = Services(); await CreateDatabaseAsync("selected");
        var selected = await provider.GetRequiredService<ICompaniesDatabaseBackupService>().CreateBackupAsync(default); File.Delete(DatabasePath);
        var result = await provider.GetRequiredService<ICompaniesDatabaseRestoreService>().RestoreAsync(Path.GetFileName(selected.BackupPath!), default);
        result.Succeeded.Should().BeTrue(); result.CurrentDatabaseExisted.Should().BeFalse(); result.SafetyBackupCreated.Should().BeFalse();
        (await MarkerAsync(DatabasePath)).Should().Be("selected");
    }

    [Fact]
    public async Task Restore_staging_survives_retention_when_max_backups_is_one()
    {
        using var provider = Services(maxBackups: 1); await CreateDatabaseAsync("selected");
        var selected = await provider.GetRequiredService<ICompaniesDatabaseBackupService>().CreateBackupAsync(default); await SetMarkerAsync("current");
        var result = await provider.GetRequiredService<ICompaniesDatabaseRestoreService>().RestoreAsync(Path.GetFileName(selected.BackupPath!), default);
        result.Succeeded.Should().BeTrue(); (await MarkerAsync(DatabasePath)).Should().Be("selected");
        File.Exists(selected.BackupPath!).Should().BeFalse(); File.Exists(result.SafetyBackupPath!).Should().BeTrue();
        (await MarkerAsync(result.SafetyBackupPath!)).Should().Be("current");
    }

    [Fact]
    public async Task Safety_backup_failure_does_not_checkpoint_delete_sidecars_or_replace()
    {
        var operations = new FaultOperations(FaultMode.None); var maintenance = new TestMaintenance(new(true, false, 0, 0));
        using var provider = Services(operations, maintenance, backupService: new FailingBackupService()); await CreateDatabaseAsync("selected");
        var selectedName = await CreateBackupDatabaseAsync("selected", ["20260725183029_InitialCompaniesPersistence"]); await SetMarkerAsync("current");
        await File.WriteAllTextAsync(DatabasePath + "-wal", "wal"); await File.WriteAllTextAsync(DatabasePath + "-shm", "shm");
        var result = await provider.GetRequiredService<ICompaniesDatabaseRestoreService>().RestoreAsync(selectedName, default);
        result.FailureCode.Should().Be(CompaniesRestoreFailureCode.SafetyBackupFailed); maintenance.Calls.Should().Be(0);
        operations.ReplaceCalls.Should().Be(0); operations.MoveCalls.Should().Be(0); (await MarkerAsync(DatabasePath)).Should().Be("current");
        File.Exists(DatabasePath + "-wal").Should().BeTrue(); File.Exists(DatabasePath + "-shm").Should().BeTrue();
    }

    [Fact]
    public async Task Concurrent_restore_returns_RestoreAlreadyInProgress_for_second_call_and_releases_gate()
    {
        var maintenance = new BlockingMaintenance(); using var provider = Services(maintenance: maintenance); await CreateDatabaseAsync("selected");
        var selected = await provider.GetRequiredService<ICompaniesDatabaseBackupService>().CreateBackupAsync(default); await SetMarkerAsync("current");
        var restore = provider.GetRequiredService<ICompaniesDatabaseRestoreService>();
        var first = restore.RestoreAsync(Path.GetFileName(selected.BackupPath!), default); await maintenance.Entered.Task;
        var second = await restore.RestoreAsync(Path.GetFileName(selected.BackupPath!), default);
        second.FailureCode.Should().Be(CompaniesRestoreFailureCode.RestoreAlreadyInProgress); maintenance.Calls.Should().Be(1);
        maintenance.Release.SetResult(); (await first).Succeeded.Should().BeTrue();
        (await restore.RestoreAsync(Path.GetFileName(selected.BackupPath!), default)).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Restore_gate_is_released_after_cancellation()
    {
        using var provider = Services(); await CreateDatabaseAsync("selected"); var selected = await provider.GetRequiredService<ICompaniesDatabaseBackupService>().CreateBackupAsync(default);
        var restore = provider.GetRequiredService<ICompaniesDatabaseRestoreService>(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restore.RestoreAsync(Path.GetFileName(selected.BackupPath!), cancellation.Token));
        (await restore.RestoreAsync(Path.GetFileName(selected.BackupPath!), default)).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Restore_gate_is_released_after_failure()
    {
        using var provider = Services(); await CreateDatabaseAsync("current"); var restore = provider.GetRequiredService<ICompaniesDatabaseRestoreService>();
        (await restore.RestoreAsync("invalid", default)).Succeeded.Should().BeFalse();
        var selected = await provider.GetRequiredService<ICompaniesDatabaseBackupService>().CreateBackupAsync(default);
        (await restore.RestoreAsync(Path.GetFileName(selected.BackupPath!), default)).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Cancellation_before_validation_does_not_modify_database()
    {
        using var provider = Services(); await CreateDatabaseAsync("current"); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetRequiredService<ICompaniesDatabaseRestoreService>().RestoreAsync("invalid", cancellation.Token));
        (await MarkerAsync(DatabasePath)).Should().Be("current");
    }

    [Fact]
    public async Task Cancellation_before_checkpoint_does_not_modify_database()
    {
        using var cancellation = new CancellationTokenSource(); var maintenance = new TestMaintenance(new(true, false, 0, 0));
        using var provider = Services(maintenance: maintenance, backupService: new CancelingBackupService(cancellation)); await CreateDatabaseAsync("current");
        var selectedName = await CreateBackupDatabaseAsync("selected", ["20260725183029_InitialCompaniesPersistence"]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetRequiredService<ICompaniesDatabaseRestoreService>().RestoreAsync(selectedName, cancellation.Token));
        maintenance.Calls.Should().Be(0); (await MarkerAsync(DatabasePath)).Should().Be("current");
    }

    [Fact]
    public async Task Cancellation_after_replacement_started_completes_valid_install()
    {
        using var cancellation = new CancellationTokenSource(); var operations = new FaultOperations(FaultMode.None, replacementStarted: cancellation.Cancel);
        using var provider = Services(operations); await CreateDatabaseAsync("selected"); var selected = await provider.GetRequiredService<ICompaniesDatabaseBackupService>().CreateBackupAsync(default); await SetMarkerAsync("current");
        var result = await provider.GetRequiredService<ICompaniesDatabaseRestoreService>().RestoreAsync(Path.GetFileName(selected.BackupPath!), cancellation.Token);
        result.Succeeded.Should().BeTrue(); (await MarkerAsync(DatabasePath)).Should().Be("selected");
    }

    [Fact]
    public async Task Cancellation_after_replacement_started_completes_verified_rollback_on_validation_failure()
    {
        using var cancellation = new CancellationTokenSource(); var operations = new FaultOperations(FaultMode.CorruptAfterReplace, replacementStarted: cancellation.Cancel);
        using var provider = Services(operations); await CreateDatabaseAsync("selected"); var selected = await provider.GetRequiredService<ICompaniesDatabaseBackupService>().CreateBackupAsync(default); await SetMarkerAsync("current");
        var result = await provider.GetRequiredService<ICompaniesDatabaseRestoreService>().RestoreAsync(Path.GetFileName(selected.BackupPath!), cancellation.Token);
        result.FailureCode.Should().Be(CompaniesRestoreFailureCode.PostRestoreValidationFailed); result.RollbackSucceeded.Should().BeTrue();
        (await MarkerAsync(DatabasePath)).Should().Be("current");
    }

    [Fact]
    public async Task Cancellation_during_staging_does_not_modify_live_database_and_releases_gate()
    {
        var stager = new BlockingStager(); var operations = new FaultOperations(FaultMode.None); var maintenance = new TestMaintenance(new(true, false, 0, 0));
        using var provider = Services(operations, maintenance, stager: stager); await CreateDatabaseAsync("current"); var selectedName = await CreateBackupDatabaseAsync("selected", ["20260725183029_InitialCompaniesPersistence"]);
        using var cancellation = new CancellationTokenSource(); var restore = provider.GetRequiredService<ICompaniesDatabaseRestoreService>();
        var running = restore.RestoreAsync(selectedName, cancellation.Token); await stager.Entered.Task; cancellation.Cancel(); stager.Release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running); (await MarkerAsync(DatabasePath)).Should().Be("current");
        maintenance.Calls.Should().Be(0); operations.ReplaceCalls.Should().Be(0); operations.MoveCalls.Should().Be(0);
        (await restore.RestoreAsync(selectedName, default)).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Ambiguous_replacement_does_not_accept_valid_but_unrelated_live_database()
    {
        using var unrelatedProvider = Services(); var unrelatedName = await CreateBackupDatabaseAsync("unrelated", ["20260725183029_InitialCompaniesPersistence"]);
        var unrelatedPath = Path.Combine(BackupDirectory, unrelatedName); var operations = new FaultOperations(FaultMode.InstallUnrelatedAndThrow, unrelatedPath);
        using var provider = Services(operations); await CreateDatabaseAsync("selected");
        var selected = await provider.GetRequiredService<ICompaniesDatabaseBackupService>().CreateBackupAsync(default); await SetMarkerAsync("current");
        var result = await provider.GetRequiredService<ICompaniesDatabaseRestoreService>().RestoreAsync(Path.GetFileName(selected.BackupPath!), default);
        result.Succeeded.Should().BeFalse(); result.FailureCode.Should().Be(CompaniesRestoreFailureCode.RecoveryStateUnknown);
        (await MarkerAsync(DatabasePath)).Should().Be("unrelated");
    }

    [Fact]
    public async Task Restore_round_trips_real_EF_company_and_concurrency_token()
    {
        using var provider = Services(); Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!); var factory = provider.GetRequiredService<IDbContextFactory<CompaniesDbContext>>();
        var company = Company.Create(OrganizationId.New(), "Original Legal", "Original Display", "5260250995", "PL", CurrencyCode.Pln,
            "Europe/Warsaw", UserId.New(), DateTimeOffset.Parse("2026-07-28T10:00:00Z"));
        await using (var context = await factory.CreateDbContextAsync()) { await context.Database.MigrateAsync(); context.Companies.Add(company); await context.SaveChangesAsync(); }
        var selected = await provider.GetRequiredService<ICompaniesDatabaseBackupService>().CreateBackupAsync(default);
        await using (var context = await factory.CreateDbContextAsync()) { var live = await context.Companies.SingleAsync(); live.Rename("Changed", UserId.New(), DateTimeOffset.Parse("2026-07-28T11:00:00Z")); await context.SaveChangesAsync(); }
        (await provider.GetRequiredService<ICompaniesDatabaseRestoreService>().RestoreAsync(Path.GetFileName(selected.BackupPath!), default)).Succeeded.Should().BeTrue();
        await using var restored = await factory.CreateDbContextAsync(); var loaded = await restored.Companies.SingleAsync();
        loaded.DisplayName.Should().Be("Original Display"); loaded.Version.Value.Should().Be(1);
    }

    private ServiceProvider Services(ICompaniesRestoreFileOperations? operations = null, ICompaniesDatabaseMaintenance? maintenance = null, int maxBackups = 10, ICompaniesDatabaseBackupService? backupService = null, ICompaniesRestoreStager? stager = null)
    {
        var services = new ServiceCollection(); services.AddLogging();
        services.AddCompaniesPersistence(o => { o.DatabasePath = DatabasePath; o.BackupDirectory = BackupDirectory; o.MaxBackups = maxBackups; o.Pooling = false; });
        if (operations is not null) services.AddSingleton(operations);
        if (maintenance is not null) services.AddSingleton(maintenance);
        if (backupService is not null) services.AddSingleton(backupService);
        if (stager is not null) services.AddSingleton(stager);
        return services.BuildServiceProvider();
    }
    private async Task<string> CreateBackupDatabaseAsync(string marker, string[] migrations)
    {
        Directory.CreateDirectory(BackupDirectory); var name = CompaniesBackupFileName.Create(DateTimeOffset.Parse("2026-02-01T00:00:00Z"), Guid.NewGuid());
        var path = Path.Combine(BackupDirectory, name); await using var connection = new SqliteConnection($"Data Source={path};Pooling=False"); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = "CREATE TABLE __EFMigrationsHistory_Companies(MigrationId TEXT NOT NULL, ProductVersion TEXT NOT NULL); CREATE TABLE marker(value TEXT); INSERT INTO marker VALUES ($marker);";
        command.Parameters.AddWithValue("$marker", marker); await command.ExecuteNonQueryAsync();
        foreach (var migration in migrations) { command.CommandText = "INSERT INTO __EFMigrationsHistory_Companies VALUES ($migration, '10.0.0')"; command.Parameters.Clear(); command.Parameters.AddWithValue("$migration", migration); await command.ExecuteNonQueryAsync(); }
        return name;
    }
    private async Task CreateDatabaseAsync(string marker)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        await using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False"); await connection.OpenAsync(); await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE __EFMigrationsHistory_Companies (MigrationId TEXT NOT NULL PRIMARY KEY, ProductVersion TEXT NOT NULL); INSERT INTO __EFMigrationsHistory_Companies VALUES ('20260725183029_InitialCompaniesPersistence','10.0.0'); CREATE TABLE marker(value TEXT); INSERT INTO marker VALUES ($value);";
        command.Parameters.AddWithValue("$value", marker); await command.ExecuteNonQueryAsync();
    }
    private async Task SetMarkerAsync(string marker) { await using var c = new SqliteConnection($"Data Source={DatabasePath};Pooling=False"); await c.OpenAsync(); await using var q = c.CreateCommand(); q.CommandText = "UPDATE marker SET value=$value"; q.Parameters.AddWithValue("$value", marker); await q.ExecuteNonQueryAsync(); }
    private static async Task<string> MarkerAsync(string path) { await using var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False"); await c.OpenAsync(); await using var q = c.CreateCommand(); q.CommandText = "SELECT value FROM marker"; return (string)(await q.ExecuteScalarAsync())!; }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }

    private enum FaultMode { None, ThrowBeforeReplace, ThrowAfterRollback, ThrowAfterInstall, ThrowAfterMoveInstall, InstallUnrelatedAndThrow, CorruptAfterMove, CorruptMoveAndFailCleanup, CorruptAfterReplace, CorruptAfterReplaceAndFailRollback, CorruptAfterReplaceAndFailCleanup, ThrowDeletingWal }
    private sealed class FaultOperations(FaultMode mode, string? unrelatedPath = null, Action? replacementStarted = null) : ICompaniesRestoreFileOperations
    {
        public int ReplaceCalls { get; private set; }
        public int MoveCalls { get; private set; }
        public bool FileExists(string path) => File.Exists(path);
        public long GetLength(string path) => new FileInfo(path).Length;
        public FileAttributes GetAttributes(string path) => File.GetAttributes(path);
        public string ComputeSha256(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)); }
        public void Delete(string path) { if (mode == FaultMode.ThrowDeletingWal && path.EndsWith("companies.db-wal", StringComparison.Ordinal)) throw new IOException("Injected WAL failure"); if (mode == FaultMode.CorruptMoveAndFailCleanup && path.EndsWith("companies.db", StringComparison.Ordinal)) throw new IOException("Injected cleanup failure"); if (mode == FaultMode.CorruptAfterReplaceAndFailCleanup && path.Contains(".failed", StringComparison.Ordinal)) throw new IOException("Injected failed-artifact cleanup failure"); File.Delete(path); }
        public void Move(string source, string destination, bool overwrite) { MoveCalls++; File.Move(source, destination, overwrite); if (mode is FaultMode.CorruptAfterMove or FaultMode.CorruptMoveAndFailCleanup) File.WriteAllText(destination, "corrupt"); if (mode == FaultMode.ThrowAfterMoveInstall) throw new IOException("Injected after move"); }
        public void Replace(string source, string destination, string backup, bool ignoreMetadataErrors)
        {
            ReplaceCalls++;
            if (mode == FaultMode.ThrowBeforeReplace) throw new IOException("Injected before mutation");
            if (mode == FaultMode.ThrowAfterRollback) { File.Move(destination, backup); throw new IOException("Injected after rollback creation"); }
            if (mode == FaultMode.InstallUnrelatedAndThrow) { File.Delete(destination); File.Copy(unrelatedPath!, destination); throw new IOException("Injected unrelated live database"); }
            if (mode == FaultMode.CorruptAfterReplaceAndFailRollback && ReplaceCalls > 1) throw new IOException("Injected rollback failure");
            File.Replace(source, destination, backup, ignoreMetadataErrors);
            replacementStarted?.Invoke();
            if (mode == FaultMode.ThrowAfterInstall) throw new IOException("Injected after install");
            if (mode is FaultMode.CorruptAfterReplace or FaultMode.CorruptAfterReplaceAndFailRollback or FaultMode.CorruptAfterReplaceAndFailCleanup && ReplaceCalls == 1) File.WriteAllText(destination, "corrupt");
        }
    }
    private sealed class TestMaintenance(CompaniesDatabaseCheckpointResult result, bool createSidecars = false) : ICompaniesDatabaseMaintenance
    {
        public int Calls { get; private set; }
        public Task<CompaniesDatabaseCheckpointResult> CheckpointAsync(string databasePath, CancellationToken cancellationToken) { Calls++; if (createSidecars) { File.WriteAllText(databasePath + "-wal", "wal"); File.WriteAllText(databasePath + "-shm", "shm"); } return Task.FromResult(result); }
    }
    private sealed class RecordingRealMaintenance(SqliteConnection keeper) : ICompaniesDatabaseMaintenance
    {
        public bool WalExistedAtEntry { get; private set; }
        public long WalLengthAtEntry { get; private set; }
        public CompaniesDatabaseCheckpointResult? Result { get; private set; }
        public async Task<CompaniesDatabaseCheckpointResult> CheckpointAsync(string databasePath, CancellationToken cancellationToken)
        {
            var wal = databasePath + "-wal"; WalExistedAtEntry = File.Exists(wal); WalLengthAtEntry = new FileInfo(wal).Length;
            await keeper.DisposeAsync(); Result = await new CompaniesDatabaseMaintenance().CheckpointAsync(databasePath, cancellationToken); return Result;
        }
    }
    private sealed class BlockingMaintenance : ICompaniesDatabaseMaintenance
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public async Task<CompaniesDatabaseCheckpointResult> CheckpointAsync(string databasePath, CancellationToken cancellationToken) { Calls++; Entered.TrySetResult(); await Release.Task; return new(true, false, 0, 0); }
    }
    private sealed class FailingBackupService : ICompaniesDatabaseBackupService
    {
        public Task<CompaniesBackupResult> CreateBackupAsync(CancellationToken cancellationToken) => Task.FromResult(CompaniesBackupResult.Failure(CompaniesBackupFailureCode.BackupFailed));
    }
    private sealed class CancelingBackupService(CancellationTokenSource cancellation) : ICompaniesDatabaseBackupService
    {
        public Task<CompaniesBackupResult> CreateBackupAsync(CancellationToken cancellationToken) { cancellation.Cancel(); return Task.FromResult(CompaniesBackupResult.Success("cancel-test-safety.db")); }
    }
    private sealed class BlockingStager : ICompaniesRestoreStager
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;
        public async Task StageAsync(string source, string staging, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref calls) > 1) { await new CompaniesRestoreStager().StageAsync(source, staging, cancellationToken); return; }
            Entered.SetResult(); await Release.Task; cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
