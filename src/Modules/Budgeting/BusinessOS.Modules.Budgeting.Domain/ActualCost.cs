using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.BuildingBlocks.Domain.Primitives;

namespace BusinessOS.Modules.Budgeting.Domain;

public enum ActualCostKind { Capex, Opex }

public sealed class ActualCost
{
    private ActualCost() { }

    public ActualCostId Id { get; private set; } = ActualCostId.New();
    public BusinessProjectId ProjectId { get; private set; }
    public ActualCostKind Kind { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public Money Amount { get; private set; }
    public DateOnly IncurredOn { get; private set; }
    public string? Note { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public long Version { get; private set; } = 1;
    public DateTimeOffset? ArchivedAtUtc { get; private set; }

    public static ActualCost Create(BusinessProjectId projectId, ActualCostKind kind, string name, Money amount,
        DateOnly incurredOn, string? note, DateTimeOffset now)
    {
        if (projectId.Value == Guid.Empty) throw new ArgumentException("Project is required.", nameof(projectId));
        var cost = new ActualCost { ProjectId = projectId };
        cost.SetFields(kind, name, amount, incurredOn, note);
        cost.CreatedAtUtc = cost.UpdatedAtUtc = now.ToUniversalTime();
        return cost;
    }

    public void Update(ActualCostKind kind, string name, Money amount, DateOnly incurredOn, string? note, DateTimeOffset now)
    {
        EnsureActive();
        SetFields(kind, name, amount, incurredOn, note);
        Touch(now);
    }

    public void Archive(DateTimeOffset now)
    {
        EnsureActive();
        ArchivedAtUtc = now.ToUniversalTime();
        Touch(now);
    }

    private void SetFields(ActualCostKind kind, string name, Money amount, DateOnly incurredOn, string? note)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        var normalizedName = name?.Trim() ?? string.Empty;
        if (normalizedName.Length is 0 or > 256) throw new ArgumentException("Cost name is required and must not exceed 256 characters.", nameof(name));
        if (amount.Amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be positive.");
        if (incurredOn == default) throw new ArgumentException("Incurred date is required.", nameof(incurredOn));
        var normalizedNote = note?.Trim();
        if (normalizedNote?.Length > 1000) throw new ArgumentException("Note is too long.", nameof(note));
        Kind = kind;
        Name = normalizedName;
        Amount = amount;
        IncurredOn = incurredOn;
        Note = string.IsNullOrEmpty(normalizedNote) ? null : normalizedNote;
    }

    private void EnsureActive()
    {
        if (ArchivedAtUtc is not null) throw new InvalidOperationException("Archived actual cost cannot be changed.");
    }

    private void Touch(DateTimeOffset now)
    {
        UpdatedAtUtc = now.ToUniversalTime();
        Version++;
    }
}
