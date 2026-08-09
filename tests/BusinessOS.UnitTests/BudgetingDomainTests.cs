using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.BuildingBlocks.Domain.Primitives;
using BusinessOS.Modules.Budgeting.Domain;
using Xunit;

namespace BusinessOS.UnitTests;

public sealed class BudgetingDomainTests
{
    [Fact] public void Budget_normalizes_name_and_versions_changes() { var b = Budget.Create(BusinessProjectId.New(), " Plan ", DateTimeOffset.UtcNow); Assert.Equal("Plan", b.Name); Assert.Equal("PLAN", b.NormalizedName); b.Rename(" Next ", DateTimeOffset.UtcNow); Assert.Equal(2, b.Version); }
    [Fact] public void Empty_budget_cannot_be_activated() { var b = Budget.Create(BusinessProjectId.New(), "Plan", DateTimeOffset.UtcNow); Assert.Throws<InvalidOperationException>(() => b.Activate(false, DateTimeOffset.UtcNow)); }
    [Fact] public void Archived_budget_is_read_only() { var b = Budget.Create(BusinessProjectId.New(), "Plan", DateTimeOffset.UtcNow); b.Archive(DateTimeOffset.UtcNow); Assert.Equal(BudgetStatus.Archived, b.Status); Assert.Throws<InvalidOperationException>(() => b.Rename("Other", DateTimeOffset.UtcNow)); }
    [Fact] public void Version_number_must_be_positive() => Assert.Throws<ArgumentOutOfRangeException>(() => BudgetVersion.Create(BudgetId.New(), 0, DateTimeOffset.UtcNow, null));
    [Fact] public void Line_rejects_negative_amount_and_order() { var id = BudgetVersionId.New(); Assert.Throws<ArgumentOutOfRangeException>(() => BudgetLine.Create(id, BudgetLineKind.Capex, "X", new(-1, CurrencyCode.Pln), 0, null)); Assert.Throws<ArgumentOutOfRangeException>(() => BudgetLine.Create(id, BudgetLineKind.Opex, "X", new(1, CurrencyCode.Pln), -1, null)); }
    [Fact] public void Snapshot_copy_has_new_identifiers_and_preserves_value() { var a = BudgetLine.Create(BudgetVersionId.New(), BudgetLineKind.Capex, "Equipment", new(100000, CurrencyCode.Pln), 0, null); var b = a.CopyTo(BudgetVersionId.New()); Assert.NotEqual(a.Id, b.Id); Assert.NotEqual(a.VersionId, b.VersionId); Assert.Equal(a.Amount, b.Amount); }
}
