using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.BuildingBlocks.Domain.Primitives;
using BusinessOS.Modules.BusinessProjects.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace BusinessOS.Modules.BusinessProjects.Application;

public static class BusinessProjectsServices
{
    public static IServiceCollection AddBusinessProjectsModule(this IServiceCollection services)
    { services.AddTransient<IBusinessProjectsCrudService, BusinessProjectsCrudService>(); services.AddTransient<IBusinessProjectsCompanyConstraintReader, BusinessProjectsCompanyConstraintReader>(); return services; }
}
public interface IBusinessProjectsExecutionContext { UserId UserId { get; } }
public interface IBusinessProjectCompanyAccess { Task<BusinessProjectCompanyInfo?> GetAccessibleCompanyAsync(Guid companyId, CancellationToken cancellationToken); }
public sealed record BusinessProjectCompanyInfo(Guid CompanyId, string DisplayName, string BaseCurrency);
public enum BusinessProjectStatusValue { Draft, Analysis, Approved, InPreparation, InProgress, ReadyToOpen, Operating, Paused, Closed, Cancelled }
public sealed record BusinessProjectListItem(Guid Id, Guid CompanyId, string Name, string BusinessType, string Location, BusinessProjectStatusValue Status, DateOnly PlannedStartDate, DateOnly PlannedOpeningDate, string BaseCurrency, DateTimeOffset UpdatedAtUtc, long Version);
public sealed record BusinessProjectDetails(Guid Id, Guid CompanyId, string Name, string BusinessType, string Location, string Description, BusinessProjectStatusValue Status, DateOnly PlannedStartDate, DateOnly PlannedOpeningDate, string BaseCurrency, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, Guid CreatedBy, Guid UpdatedBy, long Version, IReadOnlyList<BusinessProjectStatusValue> AllowedTransitions);
public sealed record CreateBusinessProjectRequest(Guid CompanyId, string Name, string BusinessType, string Location, string Description, DateOnly PlannedStartDate, DateOnly PlannedOpeningDate, string BaseCurrency);
public sealed record UpdateBusinessProjectRequest(Guid ProjectId, long ExpectedVersion, string Name, string BusinessType, string Location, string Description, DateOnly PlannedStartDate, DateOnly PlannedOpeningDate, string BaseCurrency);
public sealed record ChangeBusinessProjectStatusRequest(Guid ProjectId, long ExpectedVersion, BusinessProjectStatusValue TargetStatus);
public sealed record ArchiveBusinessProjectRequest(Guid ProjectId, long ExpectedVersion);
public enum BusinessProjectOperationStatus { Success, ValidationFailed, CompanyNotFound, ProjectNotFound, ConcurrencyConflict, DuplicateProjectName, InvalidStatusTransition, DependentRecordsExist, PersistenceFailure, Cancelled }
public sealed record BusinessProjectOperationResult(BusinessProjectOperationStatus Status, string SafeMessage, IReadOnlyDictionary<string, string[]> ValidationErrors);
public sealed record BusinessProjectOperationResult<T>(BusinessProjectOperationStatus Status, string SafeMessage, IReadOnlyDictionary<string, string[]> ValidationErrors, T? Value);
public interface IBusinessProjectsCrudService
{
    Task<IReadOnlyList<BusinessProjectListItem>> ListAsync(Guid companyId, BusinessProjectStatusValue? status, CancellationToken cancellationToken);
    Task<BusinessProjectDetails?> GetAsync(Guid projectId, CancellationToken cancellationToken);
    Task<BusinessProjectOperationResult<BusinessProjectDetails>> CreateAsync(CreateBusinessProjectRequest request, CancellationToken cancellationToken);
    Task<BusinessProjectOperationResult<BusinessProjectDetails>> UpdateAsync(UpdateBusinessProjectRequest request, CancellationToken cancellationToken);
    Task<BusinessProjectOperationResult<BusinessProjectDetails>> ChangeStatusAsync(ChangeBusinessProjectStatusRequest request, CancellationToken cancellationToken);
    Task<BusinessProjectOperationResult> ArchiveAsync(ArchiveBusinessProjectRequest request, CancellationToken cancellationToken);
    Task<bool> HasActiveProjectsAsync(Guid companyId, CancellationToken cancellationToken);
}
public enum BusinessProjectsSaveStatus { Success, ConcurrencyConflict, DuplicateProjectName }
public sealed class BusinessProjectsPersistenceException(string message, Exception innerException) : Exception(message, innerException);
public sealed class BusinessProjectsReadException : Exception { internal BusinessProjectsReadException(Exception innerException) : base("BusinessProjects read failed.", innerException) { } }
public sealed class BusinessProjectCompanyAccessException(string message, Exception innerException) : Exception(message, innerException);
public interface IBusinessProjectsCompanyConstraintReader { Task<bool> HasNonArchivedProjectsAsync(Guid companyId, CancellationToken cancellationToken); }
public interface IBusinessProjectsStore
{
    Task<IReadOnlyList<BusinessProject>> ListAsync(CompanyId companyId, BusinessProjectStatus? status, CancellationToken cancellationToken);
    Task<BusinessProject?> GetAsync(BusinessProjectId projectId, bool tracked, CancellationToken cancellationToken);
    Task<bool> NameExistsAsync(CompanyId companyId, string name, BusinessProjectId? exceptId, CancellationToken cancellationToken);
    Task<bool> HasActiveProjectsAsync(CompanyId companyId, CancellationToken cancellationToken);
    Task AddAsync(BusinessProject project, CancellationToken cancellationToken);
    Task<BusinessProjectsSaveStatus> SaveChangesAsync(CancellationToken cancellationToken);
    Task ResetTrackingAsync() => Task.CompletedTask;
}
internal sealed class BusinessProjectsCompanyConstraintReader(IBusinessProjectsStore store) : IBusinessProjectsCompanyConstraintReader
{ public Task<bool> HasNonArchivedProjectsAsync(Guid companyId, CancellationToken cancellationToken) => store.HasActiveProjectsAsync(new(companyId), cancellationToken); }
internal sealed class BusinessProjectsCrudService(IBusinessProjectsStore store, IBusinessProjectCompanyAccess companies, IBusinessProjectsExecutionContext executionContext, TimeProvider timeProvider) : IBusinessProjectsCrudService
{
    private static readonly IReadOnlyDictionary<string, string[]> Empty = new Dictionary<string, string[]>();
    public async Task<IReadOnlyList<BusinessProjectListItem>> ListAsync(Guid companyId, BusinessProjectStatusValue? status, CancellationToken ct)
    { try { if (status is not null && !Enum.IsDefined(status.Value)) throw new ArgumentException("Unsupported status.", nameof(status)); if (await companies.GetAccessibleCompanyAsync(companyId, ct) is null) return []; return (await store.ListAsync(new(companyId), status is null ? null : ToDomain(status.Value), ct)).Select(MapList).ToArray(); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } catch (Exception e) when (e is BusinessProjectsPersistenceException or BusinessProjectCompanyAccessException or ArgumentException) { throw new BusinessProjectsReadException(e); } }
    public async Task<BusinessProjectDetails?> GetAsync(Guid id, CancellationToken ct)
    { try { var p = await store.GetAsync(new(id), false, ct); return p is null || await companies.GetAccessibleCompanyAsync(p.CompanyId.Value, ct) is null ? null : Map(p); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } catch (Exception e) when (e is BusinessProjectsPersistenceException or BusinessProjectCompanyAccessException) { throw new BusinessProjectsReadException(e); } }
    public async Task<BusinessProjectOperationResult<BusinessProjectDetails>> CreateAsync(CreateBusinessProjectRequest r, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested(); if (await companies.GetAccessibleCompanyAsync(r.CompanyId, ct) is null) return Fail<BusinessProjectDetails>(BusinessProjectOperationStatus.CompanyNotFound, "Nie znaleziono aktywnej firmy.");
            var p = BusinessProject.Create(new(r.CompanyId), r.Name, r.BusinessType, r.Location, r.Description, r.PlannedStartDate, r.PlannedOpeningDate, Currency(r.BaseCurrency), executionContext.UserId, timeProvider.GetUtcNow());
            if (await store.NameExistsAsync(p.CompanyId, p.Name, null, ct)) return Fail<BusinessProjectDetails>(BusinessProjectOperationStatus.DuplicateProjectName, "Projekt o tej nazwie już istnieje w firmie.");
            await store.AddAsync(p, ct); return Saved<BusinessProjectDetails>(await store.SaveChangesAsync(ct), p, "Projekt został utworzony.");
        }
        catch (Exception e) { return MutationFailure<BusinessProjectDetails>(e, ct); }
        finally { await store.ResetTrackingAsync(); }
    }
    public async Task<BusinessProjectOperationResult<BusinessProjectDetails>> UpdateAsync(UpdateBusinessProjectRequest r, CancellationToken ct)
    {
        try
        {
            var p = await store.GetAsync(new(r.ProjectId), true, ct); if (p is null) return Fail<BusinessProjectDetails>(BusinessProjectOperationStatus.ProjectNotFound, "Nie znaleziono projektu."); if (await companies.GetAccessibleCompanyAsync(p.CompanyId.Value, ct) is null) return Fail<BusinessProjectDetails>(BusinessProjectOperationStatus.CompanyNotFound, "Nie znaleziono aktywnej firmy."); if (p.Version.Value != r.ExpectedVersion) return Conflict<BusinessProjectDetails>();
            var validated = BusinessProject.Create(p.CompanyId, r.Name, r.BusinessType, r.Location, r.Description, r.PlannedStartDate, r.PlannedOpeningDate, Currency(r.BaseCurrency), executionContext.UserId, timeProvider.GetUtcNow()); if (await store.NameExistsAsync(p.CompanyId, validated.Name, p.Id, ct)) return Fail<BusinessProjectDetails>(BusinessProjectOperationStatus.DuplicateProjectName, "Projekt o tej nazwie już istnieje w firmie."); p.Update(r.Name, r.BusinessType, r.Location, r.Description, r.PlannedStartDate, r.PlannedOpeningDate, Currency(r.BaseCurrency), executionContext.UserId, timeProvider.GetUtcNow()); return Saved<BusinessProjectDetails>(await store.SaveChangesAsync(ct), p, "Zmiany zostały zapisane.");
        }
        catch (Exception e) { return MutationFailure<BusinessProjectDetails>(e, ct); }
        finally { await store.ResetTrackingAsync(); }
    }
    public async Task<BusinessProjectOperationResult<BusinessProjectDetails>> ChangeStatusAsync(ChangeBusinessProjectStatusRequest r, CancellationToken ct)
    { try { var p = await store.GetAsync(new(r.ProjectId), true, ct); if (p is null) return Fail<BusinessProjectDetails>(BusinessProjectOperationStatus.ProjectNotFound, "Nie znaleziono projektu."); if (await companies.GetAccessibleCompanyAsync(p.CompanyId.Value, ct) is null) return Fail<BusinessProjectDetails>(BusinessProjectOperationStatus.CompanyNotFound, "Nie znaleziono aktywnej firmy."); if (p.Version.Value != r.ExpectedVersion) return Conflict<BusinessProjectDetails>(); if (!Enum.IsDefined(r.TargetStatus)) return Fail<BusinessProjectDetails>(BusinessProjectOperationStatus.ValidationFailed, "Popraw wskazane dane."); p.ChangeStatus(ToDomain(r.TargetStatus), executionContext.UserId, timeProvider.GetUtcNow()); return Saved<BusinessProjectDetails>(await store.SaveChangesAsync(ct), p, "Status projektu został zmieniony."); } catch (InvalidOperationException) { return Fail<BusinessProjectDetails>(BusinessProjectOperationStatus.InvalidStatusTransition, "Wybrana zmiana statusu jest niedozwolona."); } catch (Exception e) { return MutationFailure<BusinessProjectDetails>(e, ct); } finally { await store.ResetTrackingAsync(); } }
    public async Task<BusinessProjectOperationResult> ArchiveAsync(ArchiveBusinessProjectRequest r, CancellationToken ct)
    { try { var p = await store.GetAsync(new(r.ProjectId), true, ct); if (p is null) return Plain(BusinessProjectOperationStatus.ProjectNotFound, "Nie znaleziono projektu."); if (await companies.GetAccessibleCompanyAsync(p.CompanyId.Value, ct) is null) return Plain(BusinessProjectOperationStatus.CompanyNotFound, "Nie znaleziono aktywnej firmy."); if (p.Version.Value != r.ExpectedVersion) return Plain(BusinessProjectOperationStatus.ConcurrencyConflict, "Projekt został zmieniony. Odśwież dane."); p.SoftDelete(executionContext.UserId, timeProvider.GetUtcNow()); return (await store.SaveChangesAsync(ct)) switch { BusinessProjectsSaveStatus.Success => Plain(BusinessProjectOperationStatus.Success, "Projekt został zarchiwizowany."), BusinessProjectsSaveStatus.ConcurrencyConflict => Plain(BusinessProjectOperationStatus.ConcurrencyConflict, "Projekt został zmieniony. Odśwież dane."), _ => Plain(BusinessProjectOperationStatus.PersistenceFailure, "Nie udało się zarchiwizować projektu.") }; } catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Plain(BusinessProjectOperationStatus.Cancelled, "Operacja została anulowana."); } catch (Exception e) when (e is BusinessProjectsPersistenceException or BusinessProjectCompanyAccessException) { return Plain(BusinessProjectOperationStatus.PersistenceFailure, "Nie udało się zarchiwizować projektu."); } finally { await store.ResetTrackingAsync(); } }
    public Task<bool> HasActiveProjectsAsync(Guid id, CancellationToken ct) => store.HasActiveProjectsAsync(new(id), ct);
    private static BusinessProjectOperationResult<T> Saved<T>(BusinessProjectsSaveStatus s, BusinessProject p, string message) where T : class => s switch { BusinessProjectsSaveStatus.Success => new(BusinessProjectOperationStatus.Success, message, Empty, (T)(object)Map(p)), BusinessProjectsSaveStatus.ConcurrencyConflict => Conflict<T>(), BusinessProjectsSaveStatus.DuplicateProjectName => Fail<T>(BusinessProjectOperationStatus.DuplicateProjectName, "Projekt o tej nazwie już istnieje w firmie."), _ => Fail<T>(BusinessProjectOperationStatus.PersistenceFailure, "Nie udało się zapisać projektu.") };
    private static BusinessProjectOperationResult<T> MutationFailure<T>(Exception e, CancellationToken ct) => e switch { OperationCanceledException when ct.IsCancellationRequested => Fail<T>(BusinessProjectOperationStatus.Cancelled, "Operacja została anulowana."), ArgumentException => new(BusinessProjectOperationStatus.ValidationFailed, "Popraw wskazane dane.", new Dictionary<string, string[]> { [((ArgumentException)e).ParamName ?? "Project"] = ["Wartość jest nieprawidłowa."] }, default), BusinessProjectsPersistenceException or BusinessProjectCompanyAccessException => Fail<T>(BusinessProjectOperationStatus.PersistenceFailure, "Nie udało się zapisać projektu."), _ => throw e };
    private static CurrencyCode Currency(string value) { if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Currency required.", nameof(value)); return new(value.Trim().ToUpperInvariant()); }
    private static BusinessProjectOperationResult<T> Conflict<T>() => Fail<T>(BusinessProjectOperationStatus.ConcurrencyConflict, "Projekt został zmieniony. Odśwież dane.");
    private static BusinessProjectOperationResult<T> Fail<T>(BusinessProjectOperationStatus s, string m) => new(s, m, Empty, default);
    private static BusinessProjectOperationResult Plain(BusinessProjectOperationStatus s, string m) => new(s, m, Empty);
    private static BusinessProjectListItem MapList(BusinessProject p) => new(p.Id.Value, p.CompanyId.Value, p.Name, p.BusinessType, p.Location, ToValue(p.Status), p.PlannedStartDate, p.PlannedOpeningDate, p.BaseCurrency.Value, p.UpdatedAt, p.Version.Value);
    private static BusinessProjectDetails Map(BusinessProject p) => new(p.Id.Value, p.CompanyId.Value, p.Name, p.BusinessType, p.Location, p.Description, ToValue(p.Status), p.PlannedStartDate, p.PlannedOpeningDate, p.BaseCurrency.Value, p.CreatedAt, p.UpdatedAt, p.CreatedBy.Value, p.UpdatedBy.Value, p.Version.Value, p.AllowedTransitions.Select(ToValue).ToArray());
    private static BusinessProjectStatus ToDomain(BusinessProjectStatusValue s) => Enum.IsDefined(s) ? (BusinessProjectStatus)s : throw new ArgumentException("Unsupported status.", nameof(s));
    private static BusinessProjectStatusValue ToValue(BusinessProjectStatus s) => (BusinessProjectStatusValue)s;
}
