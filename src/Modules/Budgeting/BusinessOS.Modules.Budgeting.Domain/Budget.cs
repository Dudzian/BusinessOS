using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.BuildingBlocks.Domain.Primitives;

namespace BusinessOS.Modules.Budgeting.Domain;

public enum BudgetStatus { Draft, Active, Archived }
public enum BudgetLineKind { Capex, Opex, Revenue, Financing }

public sealed class Budget
{
    private Budget() { }
    public BudgetId Id { get; private set; } = BudgetId.New();
    public BusinessProjectId ProjectId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string NormalizedName { get; private set; } = string.Empty;
    public BudgetStatus Status { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public long Version { get; private set; } = 1;
    public DateTimeOffset? ArchivedAtUtc { get; private set; }

    public static Budget Create(BusinessProjectId projectId, string name, DateTimeOffset now)
    {
        if (projectId.Value == Guid.Empty) throw new ArgumentException("Project is required.", nameof(projectId));
        var normalized = NormalizeName(name);
        var utc = now.ToUniversalTime();
        return new Budget { ProjectId = projectId, Name = normalized, NormalizedName = NormalizeKey(normalized), CreatedAtUtc = utc, UpdatedAtUtc = utc };
    }

    public void Rename(string name, DateTimeOffset now) { EnsureDraft(); Name = NormalizeName(name); NormalizedName = NormalizeKey(Name); Touch(now); }
    public void Activate(bool hasLines, DateTimeOffset now) { EnsureDraft(); if (!hasLines) throw new InvalidOperationException("An empty budget cannot be activated."); Status = BudgetStatus.Active; Touch(now); }
    public void Archive(DateTimeOffset now) { EnsureNotArchived(); Status = BudgetStatus.Archived; ArchivedAtUtc = now.ToUniversalTime(); Touch(now); }
    public void RegisterRevision(DateTimeOffset now) { EnsureDraft(); Touch(now); }
    private void EnsureDraft() { if (Status != BudgetStatus.Draft) throw new InvalidOperationException("Only a draft budget can be changed."); }
    private void EnsureNotArchived() { if (Status == BudgetStatus.Archived) throw new InvalidOperationException("Archived budget cannot be changed."); }
    private void Touch(DateTimeOffset now) { UpdatedAtUtc = now.ToUniversalTime(); Version++; }
    public static string NormalizeName(string? value) { var result = value?.Trim() ?? string.Empty; if (result.Length is 0 or > 256) throw new ArgumentException("Budget name is required and must not exceed 256 characters.", nameof(value)); return result; }
    public static string NormalizeKey(string value) => value.Trim().ToUpperInvariant();
}

public sealed class BudgetVersion
{
    private BudgetVersion() { }
    public BudgetVersionId Id { get; private set; } = BudgetVersionId.New();
    public BudgetId BudgetId { get; private set; }
    public int Number { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public string? Note { get; private set; }
    public static BudgetVersion Create(BudgetId budgetId, int number, DateTimeOffset now, string? note)
    { if (budgetId.Value == Guid.Empty) throw new ArgumentException("Budget is required."); if (number < 1) throw new ArgumentOutOfRangeException(nameof(number)); var n = note?.Trim(); if (n?.Length > 1000) throw new ArgumentException("Note is too long.", nameof(note)); return new() { BudgetId = budgetId, Number = number, CreatedAtUtc = now.ToUniversalTime(), Note = string.IsNullOrEmpty(n) ? null : n }; }
}

public sealed class BudgetLine
{
    private BudgetLine() { }
    public Guid Id { get; private set; }
    public BudgetVersionId VersionId { get; private set; }
    public BudgetLineKind Kind { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public Money Amount { get; private set; }
    public int SortOrder { get; private set; }
    public string? Note { get; private set; }
    public static BudgetLine Create(BudgetVersionId versionId, BudgetLineKind kind, string name, Money amount, int sortOrder, string? note, Guid? id = null)
    { var line = new BudgetLine { Id = id ?? Guid.NewGuid(), VersionId = versionId }; line.Update(kind, name, amount, sortOrder, note); return line; }
    public void Update(BudgetLineKind kind, string name, Money amount, int sortOrder, string? note)
    { if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind)); var n = name?.Trim() ?? string.Empty; if (n.Length is 0 or > 256) throw new ArgumentException("Line name is required and must fit the limit.", nameof(name)); if (amount.Amount < 0) throw new ArgumentOutOfRangeException(nameof(amount)); if (sortOrder < 0) throw new ArgumentOutOfRangeException(nameof(sortOrder)); var text = note?.Trim(); if (text?.Length > 1000) throw new ArgumentException("Note is too long.", nameof(note)); Kind = kind; Name = n; Amount = amount; SortOrder = sortOrder; Note = string.IsNullOrEmpty(text) ? null : text; }
    public BudgetLine CopyTo(BudgetVersionId versionId) => Create(versionId, Kind, Name, Amount, SortOrder, Note);
}
