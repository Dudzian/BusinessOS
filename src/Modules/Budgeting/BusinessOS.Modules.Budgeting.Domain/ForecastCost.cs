using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.BuildingBlocks.Domain.Primitives;

namespace BusinessOS.Modules.Budgeting.Domain;

public enum ForecastCostKind { Capex, Opex }

public sealed class ForecastCost
{
    private ForecastCost() { }

    public ForecastCostId Id { get; private set; } = ForecastCostId.New();
    public BusinessProjectId ProjectId { get; private set; }
    public ForecastCostKind Kind { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public Money Money { get; private set; }
    public DateOnly ExpectedOn { get; private set; }
    public string? Note { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public long Version { get; private set; } = 1;
    public DateTimeOffset? ArchivedAtUtc { get; private set; }

    public static ForecastCost Create(BusinessProjectId projectId, ForecastCostKind kind, string name, Money money,
        DateOnly expectedOn, string? note, DateTimeOffset now)
    {
        if (projectId.Value == Guid.Empty) throw new ArgumentException("Project is required.", nameof(projectId));
        var cost = new ForecastCost { ProjectId = projectId };
        cost.SetFields(kind, name, money, expectedOn, note);
        cost.CreatedAtUtc = cost.UpdatedAtUtc = now.ToUniversalTime();
        return cost;
    }

    public void Update(ForecastCostKind kind, string name, Money money, DateOnly expectedOn, string? note, DateTimeOffset now)
    {
        EnsureActive();
        SetFields(kind, name, money, expectedOn, note);
        Touch(now);
    }

    public void Archive(DateTimeOffset now)
    {
        EnsureActive();
        ArchivedAtUtc = now.ToUniversalTime();
        Touch(now);
    }

    private void SetFields(ForecastCostKind kind, string name, Money money, DateOnly expectedOn, string? note)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        var normalizedName = name?.Trim() ?? string.Empty;
        if (normalizedName.Length is 0 or > 256) throw new ArgumentException("Cost name is required and must not exceed 256 characters.", nameof(name));
        if (money.Amount <= 0) throw new ArgumentOutOfRangeException(nameof(money), "Amount must be positive.");
        if (expectedOn == default) throw new ArgumentException("Expected date is required.", nameof(expectedOn));
        var normalizedNote = note?.Trim();
        if (normalizedNote?.Length > 1000) throw new ArgumentException("Note is too long.", nameof(note));
        Kind = kind;
        Name = normalizedName;
        Money = money;
        ExpectedOn = expectedOn;
        Note = string.IsNullOrEmpty(normalizedNote) ? null : normalizedNote;
    }

    private void EnsureActive()
    {
        if (ArchivedAtUtc is not null) throw new InvalidOperationException("Archived forecast cost cannot be changed.");
    }

    private void Touch(DateTimeOffset now)
    {
        UpdatedAtUtc = now.ToUniversalTime();
        Version++;
    }
}
