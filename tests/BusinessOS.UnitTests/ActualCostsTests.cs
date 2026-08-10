using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.BuildingBlocks.Domain.Primitives;
using BusinessOS.Desktop.ViewModels;
using BusinessOS.Modules.Budgeting.Application;
using BusinessOS.Modules.Budgeting.Domain;
using FluentAssertions;
using Xunit;

namespace BusinessOS.UnitTests;

public sealed class ActualCostDomainTests
{
    private static readonly BusinessProjectId Project = BusinessProjectId.New();
    private static readonly DateOnly Date = new(2026, 8, 10);
    private static Money Pln(decimal amount) => new(amount, new("PLN"));
    private static ActualCost Cost(ActualCostKind kind = ActualCostKind.Capex) => ActualCost.Create(Project, kind, "  Rent  ", Pln(10), Date, " note ", DateTimeOffset.Parse("2026-08-10T10:00:00Z"));

    [Theory][InlineData(ActualCostKind.Capex)][InlineData(ActualCostKind.Opex)] public void Create_normalizes_and_preserves_contract(ActualCostKind kind) { var cost = Cost(kind); cost.Id.Value.Should().NotBeEmpty(); cost.ProjectId.Should().Be(Project); cost.Kind.Should().Be(kind); cost.Name.Should().Be("Rent"); cost.Note.Should().Be("note"); cost.Amount.Should().Be(Pln(10)); cost.IncurredOn.Should().Be(Date); cost.Version.Should().Be(1); }
    [Fact] public void Undefined_kind_is_rejected() => FluentActions.Invoking(() => ActualCost.Create(Project, (ActualCostKind)999, "x", Pln(1), Date, null, DateTimeOffset.UtcNow)).Should().Throw<ArgumentOutOfRangeException>();
    [Theory][InlineData("")][InlineData("   ")] public void Empty_name_is_rejected(string name) => FluentActions.Invoking(() => ActualCost.Create(Project, ActualCostKind.Capex, name, Pln(1), Date, null, DateTimeOffset.UtcNow)).Should().Throw<ArgumentException>();
    [Fact] public void Too_long_name_is_rejected() => FluentActions.Invoking(() => ActualCost.Create(Project, ActualCostKind.Capex, new string('x', 257), Pln(1), Date, null, DateTimeOffset.UtcNow)).Should().Throw<ArgumentException>();
    [Theory][InlineData(0)][InlineData(-1)] public void Non_positive_amount_is_rejected(decimal amount) => FluentActions.Invoking(() => ActualCost.Create(Project, ActualCostKind.Capex, "x", Pln(amount), Date, null, DateTimeOffset.UtcNow)).Should().Throw<ArgumentOutOfRangeException>();
    [Fact] public void Default_date_is_rejected_on_create() => FluentActions.Invoking(() => ActualCost.Create(Project, ActualCostKind.Capex, "x", Pln(1), default, null, DateTimeOffset.UtcNow)).Should().Throw<ArgumentException>();
    [Fact] public void Default_date_is_rejected_on_update_without_mutation() { var cost = Cost(); FluentActions.Invoking(() => cost.Update(ActualCostKind.Opex, "new", Pln(2), default, null, DateTimeOffset.UtcNow)).Should().Throw<ArgumentException>(); cost.Version.Should().Be(1); cost.Name.Should().Be("Rent"); }
    [Fact] public void Whitespace_note_becomes_null() => ActualCost.Create(Project, ActualCostKind.Capex, "x", Pln(1), Date, "   ", DateTimeOffset.UtcNow).Note.Should().BeNull();
    [Fact] public void Too_long_note_is_rejected() => FluentActions.Invoking(() => ActualCost.Create(Project, ActualCostKind.Capex, "x", Pln(1), Date, new string('x', 1001), DateTimeOffset.UtcNow)).Should().Throw<ArgumentException>();
    [Fact] public void Update_increments_version_preserves_creation_and_changes_update() { var cost = Cost(); var created = cost.CreatedAtUtc; cost.Update(ActualCostKind.Opex, "new", Pln(20), Date.AddDays(1), null, created.AddHours(1)); cost.Version.Should().Be(2); cost.CreatedAtUtc.Should().Be(created); cost.UpdatedAtUtc.Should().Be(created.AddHours(1)); cost.Kind.Should().Be(ActualCostKind.Opex); }
    [Fact] public void Archive_is_soft_and_protected() { var cost = Cost(); cost.Archive(cost.CreatedAtUtc.AddHours(1)); cost.Version.Should().Be(2); cost.ArchivedAtUtc.Should().NotBeNull(); FluentActions.Invoking(() => cost.Archive(DateTimeOffset.UtcNow)).Should().Throw<InvalidOperationException>(); FluentActions.Invoking(() => cost.Update(ActualCostKind.Capex, "x", Pln(1), Date, null, DateTimeOffset.UtcNow)).Should().Throw<InvalidOperationException>(); }
}

public sealed class ActualCostsApplicationTests
{
    private readonly FakeStore store = new(); private readonly FakeProjects projects = new(); private readonly ActualCostsCrudService service;
    public ActualCostsApplicationTests() { projects.Item = new(Guid.NewGuid(), "Gym", "PLN", true); service = new(store, projects, new FixedTimeProvider()); }
    [Fact] public async Task Create_uses_project_currency_and_safe_mapping() { var r = await service.CreateAsync(projects.Item!.Id, ActualCostKind.Capex, " Cost ", 12, "pln", new(2026, 1, 2), " note ", default); r.Status.Should().Be(ActualCostOperationStatus.Success); r.Value!.Name.Should().Be("Cost"); r.Value.Currency.Should().Be("PLN"); }
    [Fact] public async Task Currency_mismatch_is_validation_failure() => (await service.CreateAsync(projects.Item!.Id, ActualCostKind.Capex, "x", 1, "EUR", new(2026, 1, 1), null, default)).Status.Should().Be(ActualCostOperationStatus.ValidationFailure);
    [Fact] public async Task Unavailable_project_is_rejected() { projects.Item = projects.Item! with { Available = false }; (await service.CreateAsync(projects.Item.Id, ActualCostKind.Capex, "x", 1, "PLN", new(2026, 1, 1), null, default)).Status.Should().Be(ActualCostOperationStatus.ProjectUnavailable); }
    [Fact] public async Task Update_and_archive_enforce_version() { var made = await service.CreateAsync(projects.Item!.Id, ActualCostKind.Capex, "x", 1, "PLN", new(2026, 1, 1), null, default); (await service.UpdateAsync(made.Value!.Id, 99, ActualCostKind.Opex, "y", 2, "PLN", new(2026, 1, 2), null, default)).Status.Should().Be(ActualCostOperationStatus.ConcurrencyConflict); var updated = await service.UpdateAsync(made.Value.Id, 1, ActualCostKind.Opex, "y", 2, "PLN", new(2026, 1, 2), null, default); updated.Value!.Version.Should().Be(2); (await service.ArchiveAsync(updated.Value.Id, 2, default)).Status.Should().Be(ActualCostOperationStatus.Success); }
    [Fact] public async Task Missing_update_and_archive_return_not_found() { (await service.UpdateAsync(Guid.NewGuid(), 1, ActualCostKind.Opex, "y", 2, "PLN", new(2026, 1, 2), null, default)).Status.Should().Be(ActualCostOperationStatus.NotFound); (await service.ArchiveAsync(Guid.NewGuid(), 1, default)).Status.Should().Be(ActualCostOperationStatus.NotFound); }
    [Fact] public async Task Cancelled_write_is_safe() { using var cts = new CancellationTokenSource(); cts.Cancel(); var r = await service.CreateAsync(projects.Item!.Id, ActualCostKind.Capex, "secret", 1, "PLN", new(2026, 1, 1), null, cts.Token); r.Status.Should().Be(ActualCostOperationStatus.Cancelled); r.SafeMessage.Should().NotContain("secret"); }
    [Fact] public async Task Persistence_failure_has_safe_message() { store.Failure = true; var r = await service.CreateAsync(projects.Item!.Id, ActualCostKind.Capex, "technical-secret", 1, "PLN", new(2026, 1, 1), null, default); r.Status.Should().Be(ActualCostOperationStatus.PersistenceFailure); r.SafeMessage.Should().NotContain("technical-secret"); }
    [Fact] public async Task Read_failure_is_translated() { store.Failure = true; await FluentActions.Awaiting(() => service.ListAsync(projects.Item!.Id, default)).Should().ThrowAsync<ActualCostsReadException>(); }

    private sealed class FakeProjects : IBudgetingProjectLookup { public BudgetProjectInfo? Item { get; set; } public Task<BudgetProjectInfo?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Item?.Id == id ? Item : null); public Task<IReadOnlyList<BudgetProjectInfo>> ListAvailableAsync(CancellationToken ct) => Task.FromResult((IReadOnlyList<BudgetProjectInfo>)(Item is null ? [] : [Item])); }
    private sealed class FakeStore : IActualCostsStore { public ActualCost? Value; public bool Failure; public Task<IReadOnlyList<ActualCost>> ListAsync(BusinessProjectId id, CancellationToken ct) => Failure ? throw new ActualCostsPersistenceException("technical-secret", new Exception()) : Task.FromResult((IReadOnlyList<ActualCost>)(Value is null ? [] : [Value])); public Task<ActualCost?> GetAsync(ActualCostId id, bool tracked, CancellationToken ct) => Task.FromResult(Value?.Id == id ? Value : null); public Task AddAsync(ActualCost cost, CancellationToken ct) { if (Failure) throw new ActualCostsPersistenceException("technical-secret", new Exception()); Value = cost; return Task.CompletedTask; } public Task<ActualCostOperationStatus> SaveAsync(CancellationToken ct) => Task.FromResult(ActualCostOperationStatus.Success); public Task ResetTrackingAsync() => Task.CompletedTask; }
    private sealed class FixedTimeProvider : TimeProvider { public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-08-10T10:00:00Z"); }
}
