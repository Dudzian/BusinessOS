using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.BuildingBlocks.Domain.Primitives;

namespace BusinessOS.Modules.Budgeting.Domain;

public enum BudgetLineKind
{
    Capex,
    Opex,
    Revenue,
    Financing,
}

public sealed record Budget(
    BudgetId Id,
    BusinessProjectId ProjectId,
    string Name);

public sealed record BudgetVersion(
    BudgetVersionId Id,
    BudgetId BudgetId,
    int Number,
    DateTimeOffset CreatedAt);

public sealed record BudgetLine(
    Guid Id,
    BudgetVersionId VersionId,
    BudgetLineKind Kind,
    string Name,
    Money Amount);
