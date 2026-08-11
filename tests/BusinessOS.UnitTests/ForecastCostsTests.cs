using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.BuildingBlocks.Domain.Primitives;
using BusinessOS.Desktop.ViewModels;
using BusinessOS.Modules.Budgeting.Application;
using BusinessOS.Modules.Budgeting.Domain;
using FluentAssertions;
using Xunit;

namespace BusinessOS.UnitTests;

public sealed class ForecastCostDomainTests
{
    private static readonly BusinessProjectId Project = BusinessProjectId.New();
    private static readonly DateOnly Date = new(2026, 8, 10);
    private static Money Pln(decimal amount) => new(amount, new("PLN"));
    private static ForecastCost Cost(ForecastCostKind kind = ForecastCostKind.Capex) => ForecastCost.Create(Project, kind, "  Rent  ", Pln(10), Date, " note ", DateTimeOffset.Parse("2026-08-10T10:00:00Z"));

    [Theory][InlineData(ForecastCostKind.Capex)][InlineData(ForecastCostKind.Opex)] public void Create_normalizes_and_preserves_contract(ForecastCostKind kind) { var cost = Cost(kind); cost.Id.Value.Should().NotBeEmpty(); cost.ProjectId.Should().Be(Project); cost.Kind.Should().Be(kind); cost.Name.Should().Be("Rent"); cost.Note.Should().Be("note"); cost.Money.Should().Be(Pln(10)); cost.ExpectedOn.Should().Be(Date); cost.Version.Should().Be(1); }
    [Fact] public void Undefined_kind_is_rejected() => FluentActions.Invoking(() => ForecastCost.Create(Project, (ForecastCostKind)999, "x", Pln(1), Date, null, DateTimeOffset.UtcNow)).Should().Throw<ArgumentOutOfRangeException>();
    [Theory][InlineData("")][InlineData("   ")] public void Empty_name_is_rejected(string name) => FluentActions.Invoking(() => ForecastCost.Create(Project, ForecastCostKind.Capex, name, Pln(1), Date, null, DateTimeOffset.UtcNow)).Should().Throw<ArgumentException>();
    [Fact] public void Too_long_name_is_rejected() => FluentActions.Invoking(() => ForecastCost.Create(Project, ForecastCostKind.Capex, new string('x', 257), Pln(1), Date, null, DateTimeOffset.UtcNow)).Should().Throw<ArgumentException>();
    [Theory][InlineData(0)][InlineData(-1)] public void Non_positive_amount_is_rejected(decimal amount) => FluentActions.Invoking(() => ForecastCost.Create(Project, ForecastCostKind.Capex, "x", Pln(amount), Date, null, DateTimeOffset.UtcNow)).Should().Throw<ArgumentOutOfRangeException>();
    [Fact] public void Default_date_is_rejected_on_create() => FluentActions.Invoking(() => ForecastCost.Create(Project, ForecastCostKind.Capex, "x", Pln(1), default, null, DateTimeOffset.UtcNow)).Should().Throw<ArgumentException>();
    [Fact] public void Past_expected_date_is_accepted() => ForecastCost.Create(Project, ForecastCostKind.Capex, "x", Pln(1), new(2000, 1, 1), null, DateTimeOffset.UtcNow).ExpectedOn.Should().Be(new DateOnly(2000, 1, 1));
    [Fact] public void Future_expected_date_is_accepted() => ForecastCost.Create(Project, ForecastCostKind.Opex, "x", Pln(1), new(2099, 12, 31), null, DateTimeOffset.UtcNow).ExpectedOn.Should().Be(new DateOnly(2099, 12, 31));
    [Fact] public void Default_date_is_rejected_on_update_without_mutation() { var cost = Cost(); FluentActions.Invoking(() => cost.Update(ForecastCostKind.Opex, "new", Pln(2), default, null, DateTimeOffset.UtcNow)).Should().Throw<ArgumentException>(); cost.Version.Should().Be(1); cost.Name.Should().Be("Rent"); }
    [Fact] public void Whitespace_note_becomes_null() => ForecastCost.Create(Project, ForecastCostKind.Capex, "x", Pln(1), Date, "   ", DateTimeOffset.UtcNow).Note.Should().BeNull();
    [Fact] public void Too_long_note_is_rejected() => FluentActions.Invoking(() => ForecastCost.Create(Project, ForecastCostKind.Capex, "x", Pln(1), Date, new string('x', 1001), DateTimeOffset.UtcNow)).Should().Throw<ArgumentException>();
    [Fact] public void Update_increments_version_preserves_creation_and_changes_update() { var cost = Cost(); var created = cost.CreatedAtUtc; cost.Update(ForecastCostKind.Opex, "new", Pln(20), Date.AddDays(1), null, created.AddHours(1)); cost.Version.Should().Be(2); cost.CreatedAtUtc.Should().Be(created); cost.UpdatedAtUtc.Should().Be(created.AddHours(1)); cost.Kind.Should().Be(ForecastCostKind.Opex); }
    [Fact] public void Archive_is_soft_and_protected() { var cost = Cost(); cost.Archive(cost.CreatedAtUtc.AddHours(1)); cost.Version.Should().Be(2); cost.ArchivedAtUtc.Should().NotBeNull(); FluentActions.Invoking(() => cost.Archive(DateTimeOffset.UtcNow)).Should().Throw<InvalidOperationException>(); FluentActions.Invoking(() => cost.Update(ForecastCostKind.Capex, "x", Pln(1), Date, null, DateTimeOffset.UtcNow)).Should().Throw<InvalidOperationException>(); }
}

public sealed class ForecastCostsApplicationTests
{
    private readonly FakeStore store = new(); private readonly FakeProjects projects = new(); private readonly ForecastCostsCrudService service;
    public ForecastCostsApplicationTests() { projects.Item = new(Guid.NewGuid(), "Gym", "PLN", true); service = new(store, projects, new FixedTimeProvider()); }
    [Fact] public async Task Create_uses_project_currency_and_safe_mapping() { var r = await service.CreateAsync(projects.Item!.Id, ForecastCostKind.Capex, " Cost ", 12, "pln", new(2026, 1, 2), " note ", default); r.Status.Should().Be(ForecastCostOperationStatus.Success); r.Value!.Name.Should().Be("Cost"); r.Value.Currency.Should().Be("PLN"); }
    [Fact] public async Task Currency_mismatch_is_validation_failure() => (await service.CreateAsync(projects.Item!.Id, ForecastCostKind.Capex, "x", 1, "EUR", new(2026, 1, 1), null, default)).Status.Should().Be(ForecastCostOperationStatus.ValidationFailure);
    [Fact] public async Task Unavailable_project_is_rejected() { projects.Item = projects.Item! with { Available = false }; (await service.CreateAsync(projects.Item.Id, ForecastCostKind.Capex, "x", 1, "PLN", new(2026, 1, 1), null, default)).Status.Should().Be(ForecastCostOperationStatus.ProjectUnavailable); }
    [Fact] public async Task Update_and_archive_enforce_version() { var made = await service.CreateAsync(projects.Item!.Id, ForecastCostKind.Capex, "x", 1, "PLN", new(2026, 1, 1), null, default); (await service.UpdateAsync(made.Value!.Id, 99, ForecastCostKind.Opex, "y", 2, "PLN", new(2026, 1, 2), null, default)).Status.Should().Be(ForecastCostOperationStatus.ConcurrencyConflict); var updated = await service.UpdateAsync(made.Value.Id, 1, ForecastCostKind.Opex, "y", 2, "PLN", new(2026, 1, 2), null, default); updated.Value!.Version.Should().Be(2); (await service.ArchiveAsync(updated.Value.Id, 2, default)).Status.Should().Be(ForecastCostOperationStatus.Success); }
    [Fact] public async Task Missing_update_and_archive_return_not_found() { (await service.UpdateAsync(Guid.NewGuid(), 1, ForecastCostKind.Opex, "y", 2, "PLN", new(2026, 1, 2), null, default)).Status.Should().Be(ForecastCostOperationStatus.NotFound); (await service.ArchiveAsync(Guid.NewGuid(), 1, default)).Status.Should().Be(ForecastCostOperationStatus.NotFound); }
    [Fact] public async Task Cancelled_write_is_safe() { using var cts = new CancellationTokenSource(); cts.Cancel(); var r = await service.CreateAsync(projects.Item!.Id, ForecastCostKind.Capex, "secret", 1, "PLN", new(2026, 1, 1), null, cts.Token); r.Status.Should().Be(ForecastCostOperationStatus.Cancelled); r.SafeMessage.Should().NotContain("secret"); }
    [Fact] public async Task Persistence_failure_has_safe_message() { store.Failure = true; var r = await service.CreateAsync(projects.Item!.Id, ForecastCostKind.Capex, "technical-secret", 1, "PLN", new(2026, 1, 1), null, default); r.Status.Should().Be(ForecastCostOperationStatus.PersistenceFailure); r.SafeMessage.Should().NotContain("technical-secret"); }
    [Fact] public async Task Read_failure_is_translated() { store.Failure = true; await FluentActions.Awaiting(() => service.ListAsync(projects.Item!.Id, default)).Should().ThrowAsync<ForecastCostsReadException>(); }

    private sealed class FakeProjects : IBudgetingProjectLookup { public BudgetProjectInfo? Item { get; set; } public Task<BudgetProjectInfo?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Item?.Id == id ? Item : null); public Task<IReadOnlyList<BudgetProjectInfo>> ListAvailableAsync(CancellationToken ct) => Task.FromResult((IReadOnlyList<BudgetProjectInfo>)(Item is null ? [] : [Item])); }
    private sealed class FakeStore : IForecastCostsStore { public ForecastCost? Value; public bool Failure; public Task<IReadOnlyList<ForecastCost>> ListAsync(BusinessProjectId id, CancellationToken ct) => Failure ? throw new ForecastCostsPersistenceException("technical-secret", new Exception()) : Task.FromResult((IReadOnlyList<ForecastCost>)(Value is null ? [] : [Value])); public Task<ForecastCost?> GetAsync(ForecastCostId id, bool tracked, CancellationToken ct) => Task.FromResult(Value?.Id == id ? Value : null); public Task AddAsync(ForecastCost cost, CancellationToken ct) { if (Failure) throw new ForecastCostsPersistenceException("technical-secret", new Exception()); Value = cost; return Task.CompletedTask; } public Task<ForecastCostOperationStatus> SaveAsync(CancellationToken ct) => Task.FromResult(ForecastCostOperationStatus.Success); public Task ResetTrackingAsync() => Task.CompletedTask; }
    private sealed class FixedTimeProvider : TimeProvider { public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-08-10T10:00:00Z"); }
}
