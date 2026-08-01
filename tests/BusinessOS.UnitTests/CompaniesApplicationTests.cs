using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.BuildingBlocks.Domain.Primitives;
using BusinessOS.Modules.Companies.Application;
using BusinessOS.Modules.Companies.Domain;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BusinessOS.UnitTests;

public sealed class CompaniesApplicationTests
{
    private readonly FakeStore store = new();
    private readonly OrganizationId organization = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private readonly UserId user = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    [Fact] public async Task Empty_list_is_returned() => (await CreateService().ListAsync(default)).Should().BeEmpty();

    [Fact]
    public async Task Create_maps_details_and_uses_controlled_context_and_time()
    {
        var result = await CreateService().CreateAsync(new("Legal", "Display", "526-025-09-95", "pl", "pln", "Europe/Warsaw", CompanyStatusValue.Active), default);
        result.Status.Should().Be(CompanyOperationStatus.Success); result.Value!.CreatedBy.Should().Be(user.Value);
        result.Value.CreatedAtUtc.Should().Be(DateTimeOffset.Parse("2026-08-01T08:00:00Z"));
    }

    [Fact]
    public async Task Invalid_create_returns_safe_validation_result()
    {
        var result = await CreateService().CreateAsync(new("", "Display", "secret raw database.db", "PL", "PLN", "Europe/Warsaw", CompanyStatusValue.Active), default);
        result.Status.Should().Be(CompanyOperationStatus.ValidationFailed); result.SafeMessage.Should().NotContain("database.db");
    }

    [Fact]
    public async Task Duplicate_and_persistence_failures_are_translated_to_safe_results()
    {
        store.SaveStatus = CompaniesSaveStatus.DuplicateTaxIdentificationNumber;
        var duplicate = await CreateService().CreateAsync(ValidRequest(), default);
        duplicate.Status.Should().Be(CompanyOperationStatus.DuplicateTaxIdentificationNumber);
        store.FailureStage = "save";
        var failure = await CreateService().CreateAsync(ValidRequest() with { TaxIdentificationNumber = null, CountryCode = "DE", BaseCurrency = "EUR" }, default);
        failure.Status.Should().Be(CompanyOperationStatus.PersistenceFailure); failure.SafeMessage.Should().NotContain("SQLite");
    }

    [Fact]
    public async Task Update_checks_stale_version_and_save_race()
    {
        var company = Seed(); var service = CreateService();
        (await service.UpdateAsync(Update(company, 9), default)).Status.Should().Be(CompanyOperationStatus.ConcurrencyConflict);
        store.SaveStatus = CompaniesSaveStatus.ConcurrencyConflict;
        (await service.UpdateAsync(Update(company, 1), default)).Status.Should().Be(CompanyOperationStatus.ConcurrencyConflict);
    }

    [Fact]
    public async Task Archive_handles_not_found_success_and_conflict()
    {
        var service = CreateService();
        (await service.ArchiveAsync(new(Guid.NewGuid(), 1), default)).Status.Should().Be(CompanyOperationStatus.NotFound);
        var company = Seed();
        (await service.ArchiveAsync(new(company.Id.Value, 1), default)).Status.Should().Be(CompanyOperationStatus.Success);
    }

    [Fact]
    public async Task Cancellation_is_a_closed_result()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        (await CreateService().CreateAsync(ValidRequest(), cancellation.Token)).Status.Should().Be(CompanyOperationStatus.Cancelled);
    }

    [Theory]
    [InlineData("exists")]
    [InlineData("add")]
    [InlineData("save")]
    public async Task Persistence_failures_are_closed_and_safe(string stage)
    {
        store.FailureStage = stage;
        var result = await CreateService().CreateAsync(ValidRequest(), default);
        result.Status.Should().Be(CompanyOperationStatus.PersistenceFailure);
        result.SafeMessage.Should().NotContain("businessos.db").And.NotContain("Data Source").And.NotContain("SQL");
    }

    [Fact]
    public async Task Get_failure_during_update_is_closed_and_safe()
    {
        store.FailureStage = "get";
        var result = await CreateService().UpdateAsync(new(Guid.NewGuid(), 1, "Legal", "Display", null, "DE", "EUR", "Europe/Berlin", CompanyStatusValue.Active), default);
        result.Status.Should().Be(CompanyOperationStatus.PersistenceFailure);
        result.SafeMessage.Should().NotContain("businessos.db");
    }

    [Fact]
    public void Application_status_contract_excludes_archived() =>
        Enum.GetValues<CompanyStatusValue>().Should().NotContain(status => status.ToString() == "Archived");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Missing_currency_is_a_safe_validation_result(string? currency)
    {
        var result = await CreateService().CreateAsync(ValidRequest() with { BaseCurrency = currency! }, default);
        result.Status.Should().Be(CompanyOperationStatus.ValidationFailed);
        result.SafeMessage.Should().Be("Popraw wskazane dane.");
    }

    [Fact]
    public async Task Undefined_application_status_is_a_safe_validation_result()
    {
        var result = await CreateService().CreateAsync(ValidRequest() with { Status = (CompanyStatusValue)999 }, default);
        result.Status.Should().Be(CompanyOperationStatus.ValidationFailed);
        result.SafeMessage.Should().NotContain("ArgumentException");
    }

    private ICompaniesCrudService CreateService()
    {
        var services = new ServiceCollection().AddCompaniesModule()
            .AddSingleton<ICompaniesStore>(store)
            .AddSingleton<ICompaniesExecutionContext>(new Context(organization, user))
            .AddSingleton<TimeProvider>(new FixedTimeProvider(DateTimeOffset.Parse("2026-08-01T08:00:00Z")))
            .BuildServiceProvider();
        return services.GetRequiredService<ICompaniesCrudService>();
    }
    private Company Seed() { var company = Company.Create(organization, "Legal", "Display", "5260250995", "PL", CurrencyCode.Pln, "Europe/Warsaw", user, DateTimeOffset.Parse("2026-08-01T08:00:00Z")); store.Items.Add(company); return company; }
    private static CreateCompanyRequest ValidRequest() => new("Legal", "Display", "5260250995", "PL", "PLN", "Europe/Warsaw", CompanyStatusValue.Active);
    private static UpdateCompanyRequest Update(Company company, long version) => new(company.Id.Value, version, "Updated", "Updated", "5260250995", "PL", "PLN", "Europe/Warsaw", CompanyStatusValue.Active);
    private sealed record Context(OrganizationId OrganizationId, UserId UserId) : ICompaniesExecutionContext;
    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider { public override DateTimeOffset GetUtcNow() => value; }
    private sealed class FakeStore : ICompaniesStore
    {
        public List<Company> Items { get; } = []; public CompaniesSaveStatus SaveStatus { get; set; } = CompaniesSaveStatus.Success; public string? FailureStage { get; set; }
        private static CompaniesPersistenceException Failure() => new("persistence", new InvalidOperationException("Data Source=/secret/businessos.db; SQL SELECT"));
        public Task AddAsync(Company company, CancellationToken token) { if (FailureStage == "add") throw Failure(); Items.Add(company); return Task.CompletedTask; }
        public Task<Company?> GetAsync(OrganizationId org, CompanyId id, bool tracked, CancellationToken token) { if (FailureStage == "get") throw Failure(); return Task.FromResult(Items.SingleOrDefault(x => x.OrganizationId == org && x.Id == id)); }
        public Task<IReadOnlyList<Company>> ListAsync(OrganizationId org, CancellationToken token) => Task.FromResult<IReadOnlyList<Company>>(Items.Where(x => x.OrganizationId == org && !x.IsDeleted).ToArray());
        public Task<CompaniesSaveStatus> SaveChangesAsync(CancellationToken token) { if (FailureStage == "save") throw Failure(); return Task.FromResult(SaveStatus); }
        public Task<bool> TaxIdExistsAsync(OrganizationId org, string taxId, CompanyId? except, CancellationToken token) { if (FailureStage == "exists") throw Failure(); return Task.FromResult(Items.Any(x => x.OrganizationId == org && x.TaxIdentificationNumber?.Value == taxId && x.Id != except)); }
    }
}
