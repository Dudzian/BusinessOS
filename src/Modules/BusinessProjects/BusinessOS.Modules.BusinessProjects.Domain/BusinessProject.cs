using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.BuildingBlocks.Domain.Primitives;

namespace BusinessOS.Modules.BusinessProjects.Domain;

public enum BusinessProjectStatus { Draft, Analysis, Approved, InPreparation, InProgress, ReadyToOpen, Operating, Paused, Closed, Cancelled }

public sealed class BusinessProject
{
    private static readonly IReadOnlyDictionary<BusinessProjectStatus, BusinessProjectStatus[]> Transitions =
        new Dictionary<BusinessProjectStatus, BusinessProjectStatus[]>
        {
            [BusinessProjectStatus.Draft] = [BusinessProjectStatus.Analysis, BusinessProjectStatus.Cancelled],
            [BusinessProjectStatus.Analysis] = [BusinessProjectStatus.Draft, BusinessProjectStatus.Approved, BusinessProjectStatus.Cancelled],
            [BusinessProjectStatus.Approved] = [BusinessProjectStatus.InPreparation, BusinessProjectStatus.Cancelled],
            [BusinessProjectStatus.InPreparation] = [BusinessProjectStatus.InProgress, BusinessProjectStatus.Paused, BusinessProjectStatus.Cancelled],
            [BusinessProjectStatus.InProgress] = [BusinessProjectStatus.ReadyToOpen, BusinessProjectStatus.Paused, BusinessProjectStatus.Cancelled],
            [BusinessProjectStatus.ReadyToOpen] = [BusinessProjectStatus.Operating, BusinessProjectStatus.Paused, BusinessProjectStatus.Cancelled],
            [BusinessProjectStatus.Operating] = [BusinessProjectStatus.Paused, BusinessProjectStatus.Closed],
            [BusinessProjectStatus.Paused] = [BusinessProjectStatus.InPreparation, BusinessProjectStatus.InProgress, BusinessProjectStatus.ReadyToOpen, BusinessProjectStatus.Operating, BusinessProjectStatus.Cancelled],
            [BusinessProjectStatus.Closed] = [],
            [BusinessProjectStatus.Cancelled] = [],
        };

    private BusinessProject() { }
    public BusinessProjectId Id { get; private set; } = BusinessProjectId.New();
    public CompanyId CompanyId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string BusinessType { get; private set; } = string.Empty;
    public string Location { get; private set; } = string.Empty;
    public BusinessProjectStatus Status { get; private set; } = BusinessProjectStatus.Draft;
    public DateOnly PlannedStartDate { get; private set; }
    public DateOnly PlannedOpeningDate { get; private set; }
    public CurrencyCode BaseCurrency { get; private set; } = CurrencyCode.Pln;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public UserId CreatedBy { get; private set; }
    public UserId UpdatedBy { get; private set; }
    public EntityVersion Version { get; private set; } = new(1);
    public bool IsDeleted { get; private set; }
    public IReadOnlyList<BusinessProjectStatus> AllowedTransitions => Transitions.TryGetValue(Status, out var values) ? values : [];

    public static BusinessProject Create(CompanyId companyId, string name, string type, string location, string description,
        DateOnly start, DateOnly opening, CurrencyCode currency, UserId actor, DateTimeOffset now)
    {
        if (companyId.Value == Guid.Empty) throw new ArgumentException("Company is required.", nameof(companyId));
        Validate(name, type, location, description, start, opening);
        var utc = now.ToUniversalTime();
        return new BusinessProject
        {
            CompanyId = companyId,
            Name = NormalizeRequired(name, 256, nameof(name)),
            BusinessType = NormalizeRequired(type, 128, nameof(type)),
            Location = NormalizeRequired(location, 256, nameof(location)),
            Description = NormalizeOptional(description, 4000, nameof(description)),
            PlannedStartDate = start,
            PlannedOpeningDate = opening,
            BaseCurrency = new CurrencyCode(currency.Value.Trim().ToUpperInvariant()),
            CreatedBy = actor,
            UpdatedBy = actor,
            CreatedAt = utc,
            UpdatedAt = utc,
            Status = BusinessProjectStatus.Draft
        };
    }

    public void Update(string name, string type, string location, string description, DateOnly start, DateOnly opening,
        CurrencyCode currency, UserId actor, DateTimeOffset now)
    {
        EnsureActive(); Validate(name, type, location, description, start, opening);
        Name = NormalizeRequired(name, 256, nameof(name)); BusinessType = NormalizeRequired(type, 128, nameof(type));
        Location = NormalizeRequired(location, 256, nameof(location)); Description = NormalizeOptional(description, 4000, nameof(description));
        PlannedStartDate = start; PlannedOpeningDate = opening; BaseCurrency = new CurrencyCode(currency.Value.Trim().ToUpperInvariant());
        Touch(actor, now);
    }

    public void ChangeStatus(BusinessProjectStatus target, UserId actor, DateTimeOffset now)
    {
        EnsureActive();
        if (!Enum.IsDefined(target)) throw new ArgumentOutOfRangeException(nameof(target));
        if (target == Status || !AllowedTransitions.Contains(target)) throw new InvalidOperationException("Status transition is not allowed.");
        Status = target; Touch(actor, now);
    }

    public void SoftDelete(UserId actor, DateTimeOffset now) { EnsureActive(); IsDeleted = true; Touch(actor, now); }
    private void Touch(UserId actor, DateTimeOffset now) { UpdatedBy = actor; UpdatedAt = now.ToUniversalTime(); Version = Version.Next(); }
    private void EnsureActive() { if (IsDeleted) throw new InvalidOperationException("Archived project cannot be changed."); }
    private static void Validate(string name, string type, string location, string description, DateOnly start, DateOnly opening)
    {
        _ = NormalizeRequired(name, 256, nameof(name)); _ = NormalizeRequired(type, 128, nameof(type));
        _ = NormalizeRequired(location, 256, nameof(location)); _ = NormalizeOptional(description, 4000, nameof(description));
        if (opening < start) throw new ArgumentException("Opening date cannot be before start date.", nameof(opening));
    }
    private static string NormalizeRequired(string value, int max, string parameter)
    { var normalized = value?.Trim() ?? string.Empty; if (normalized.Length == 0 || normalized.Length > max) throw new ArgumentException("Value is required and must fit the limit.", parameter); return normalized; }
    private static string NormalizeOptional(string? value, int max, string parameter)
    { var normalized = value?.Trim() ?? string.Empty; if (normalized.Length > max) throw new ArgumentException("Value exceeds the limit.", parameter); return normalized; }
}
