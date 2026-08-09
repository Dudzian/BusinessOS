using BusinessOS.Modules.Companies.Infrastructure.Persistence;
using BusinessOS.Modules.BusinessProjects.Infrastructure.Persistence;
using BusinessOS.Modules.Budgeting.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace BusinessOS.AppHost;

public interface IApplicationStartupCoordinator
{
    Task<ApplicationStartupResult> InitializeAsync(CancellationToken cancellationToken);
}

public enum ApplicationStartupFailureCode
{
    None = 0,
    DatabaseInspectionFailed = 1,
    BackupFailed = 2,
    BackupIntegrityCheckFailed = 3,
    MigrationFailed = 4,
    Cancelled = 5,
    UnexpectedFailure = 6,
}

public enum ApplicationStartupStatus { Success, Failure, Cancelled }

public sealed record ApplicationStartupResult(
    ApplicationStartupStatus Status,
    bool DatabaseWasCreated,
    bool MigrationsApplied,
    bool BackupCreated,
    string? BackupPath,
    ApplicationStartupFailureCode FailureCode,
    string? UserMessage,
    string? DiagnosticId)
{
    public bool Succeeded => Status == ApplicationStartupStatus.Success;

    public static ApplicationStartupResult Success(bool created, bool migrated, string? backupPath) =>
        new(ApplicationStartupStatus.Success, created, migrated, backupPath is not null, backupPath, ApplicationStartupFailureCode.None, null, null);

    public static ApplicationStartupResult Failure(ApplicationStartupFailureCode code, string message, string diagnosticId, string? backupPath = null) =>
        new(ApplicationStartupStatus.Failure, false, false, backupPath is not null, backupPath, code, message, diagnosticId);

    public static ApplicationStartupResult Cancelled() =>
        new(ApplicationStartupStatus.Cancelled, false, false, false, null, ApplicationStartupFailureCode.Cancelled, "Uruchamianie zostało anulowane.", null);
}

public sealed class ApplicationStartupCoordinator(
    ICompaniesMigrationInspector inspector,
    ICompaniesDatabaseBackupService backupService,
    ICompaniesDatabaseInitializer initializer,
    ILogger<ApplicationStartupCoordinator> logger,
    IBusinessProjectsDatabaseLifecycle? businessProjects = null,
    IBudgetingDatabaseLifecycle? budgeting = null) : IApplicationStartupCoordinator
{
    private readonly SemaphoreSlim initializationLock = new(1, 1);

    public async Task<ApplicationStartupResult> InitializeAsync(CancellationToken cancellationToken)
    {
        await initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CompaniesMigrationState state;
            try
            {
                logger.LogInformation("Inspecting Companies database migrations.");
                state = await inspector.InspectAsync(cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Companies database has {PendingMigrationCount} pending migrations.", state.PendingMigrations.Count);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                return Failure(ApplicationStartupFailureCode.DatabaseInspectionFailed, "Nie udało się sprawdzić stanu bazy danych.", exception);
            }

            BusinessProjectsMigrationState? projectsState = null;
            IReadOnlyList<string> budgetingPending = [];
            try
            {
                if (businessProjects is not null) projectsState = await businessProjects.InspectAsync(cancellationToken).ConfigureAwait(false);
                if (budgeting is not null) budgetingPending = await budgeting.PendingAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { return Failure(ApplicationStartupFailureCode.DatabaseInspectionFailed, "Nie udało się sprawdzić stanu bazy danych.", exception); }

            var anyPending = state.HasPendingMigrations || projectsState?.HasPendingMigrations == true || budgetingPending.Count > 0;
            if (state.DatabaseExists && !anyPending) return ApplicationStartupResult.Success(false, false, null);

            string? backupPath = null;
            if (state.DatabaseExists)
            {
                CompaniesBackupResult backup;
                try
                {
                    backup = await backupService.CreateBackupAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    return Failure(ApplicationStartupFailureCode.BackupFailed, "Nie udało się utworzyć bezpiecznej kopii bazy danych.", exception);
                }

                if (!backup.Succeeded)
                {
                    var code = backup.FailureCode == CompaniesBackupFailureCode.IntegrityCheckFailed
                        ? ApplicationStartupFailureCode.BackupIntegrityCheckFailed
                        : ApplicationStartupFailureCode.BackupFailed;
                    return Failure(code, "Nie udało się utworzyć bezpiecznej kopii bazy danych.", null);
                }

                backupPath = backup.BackupPath;
            }

            try
            {
                logger.LogInformation("Starting Companies database migration.");
                await initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Companies database migration completed.");
                if (businessProjects is not null)
                {
                    logger.LogInformation("Starting BusinessProjects database migration.");
                    await businessProjects.InitializeAsync(cancellationToken).ConfigureAwait(false);
                    logger.LogInformation("BusinessProjects database migration completed.");
                }
                if (budgeting is not null)
                {
                    logger.LogInformation("Starting Budgeting database migration.");
                    await budgeting.InitializeAsync(cancellationToken).ConfigureAwait(false);
                    logger.LogInformation("Budgeting database migration completed.");
                }
                return ApplicationStartupResult.Success(!state.DatabaseExists, anyPending || !state.DatabaseExists, backupPath);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                return Failure(ApplicationStartupFailureCode.MigrationFailed, "Nie udało się zaktualizować bazy danych.", exception, backupPath);
            }
        }
        finally
        {
            initializationLock.Release();
        }
    }

    private ApplicationStartupResult Failure(ApplicationStartupFailureCode code, string message, Exception? exception, string? backupPath = null)
    {
        var diagnosticId = Guid.NewGuid().ToString("N");
        logger.LogError(exception, "Application persistence startup failed with {FailureCode}; DiagnosticId {DiagnosticId}; backup {BackupPath}.", code, diagnosticId, backupPath);
        return ApplicationStartupResult.Failure(code, message, diagnosticId, backupPath);
    }
}
