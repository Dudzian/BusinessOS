using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.BuildingBlocks.Domain.Primitives;

namespace BusinessOS.Modules.BusinessProjects.Domain;

public enum BusinessProjectStatus
{
    Draft,
    Analysis,
    Approved,
    InPreparation,
    InProgress,
    ReadyToOpen,
    Operating,
    Paused,
    Closed,
    Cancelled,
}

public sealed class BusinessProject
{
    private BusinessProject()
    {
    }

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

    public static BusinessProject Create(
        CompanyId companyId,
        string name,
        string type,
        string location,
        string description,
        DateOnly start,
        DateOnly opening,
        CurrencyCode currency,
        UserId actor,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Project name is required.", nameof(name));
        }

        if (opening < start)
        {
            throw new ArgumentException("Opening date cannot be before start date.", nameof(opening));
        }

        var normalizedNow = now.ToUniversalTime();
        return new BusinessProject
        {
            CompanyId = companyId,
            Name = name.Trim(),
            BusinessType = type.Trim(),
            Location = location.Trim(),
            Description = description.Trim(),
            PlannedStartDate = start,
            PlannedOpeningDate = opening,
            BaseCurrency = currency,
            CreatedBy = actor,
            UpdatedBy = actor,
            CreatedAt = normalizedNow,
            UpdatedAt = normalizedNow,
        };
    }

    public void SoftDelete(UserId actor, DateTimeOffset now)
    {
        IsDeleted = true;
        UpdatedBy = actor;
        UpdatedAt = now.ToUniversalTime();
        Version = Version.Next();
    }
}
