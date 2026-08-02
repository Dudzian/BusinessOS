using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.BuildingBlocks.Domain.Primitives;
using BusinessOS.Modules.BusinessProjects.Application;
using BusinessOS.Modules.BusinessProjects.Domain;
using BusinessOS.Modules.BusinessProjects.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BusinessOS.IntegrationTests;

public sealed class BusinessProjectsPersistenceTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"businessos-projects-{Guid.NewGuid():N}.db");
    private readonly UserId user = new(Guid.NewGuid());

    [Fact]
    public async Task Create_can_be_read_by_new_context_and_queries_are_company_scoped()
    {
        var firstCompany = new CompanyId(Guid.NewGuid());
        var secondCompany = new CompanyId(Guid.NewGuid());
        await using (var db = Context())
        {
            await db.Database.MigrateAsync();
            db.BusinessProjects.AddRange(Project(firstCompany, "First"), Project(secondCompany, "Second"));
            await db.SaveChangesAsync();
        }
        await using var reopened = Context();
        var projects = await reopened.BusinessProjects.AsNoTracking().Where(project => project.CompanyId == firstCompany).ToListAsync();
        projects.Should().ContainSingle().Which.Name.Should().Be("First");
    }

    [Fact]
    public async Task Soft_delete_filter_and_ignore_query_filters_are_persistent()
    {
        var project = Project(new(Guid.NewGuid()), "Archived");
        await using (var db = Context()) { await db.Database.MigrateAsync(); db.Add(project); await db.SaveChangesAsync(); }
        await using (var db = Context()) { var tracked = await db.BusinessProjects.SingleAsync(); tracked.SoftDelete(user, DateTimeOffset.UtcNow.AddMinutes(1)); await db.SaveChangesAsync(); }
        await using var read = Context();
        (await read.BusinessProjects.CountAsync()).Should().Be(0);
        (await read.BusinessProjects.IgnoreQueryFilters().SingleAsync()).IsDeleted.Should().BeTrue();
    }

    [Fact]
    public async Task Active_name_is_case_insensitive_per_company_and_reusable_after_archive()
    {
        var company = new CompanyId(Guid.NewGuid());
        await using var db = Context();
        await db.Database.MigrateAsync();
        db.Add(Project(company, "Gym")); await db.SaveChangesAsync();
        db.Add(Project(company, "gYm"));
        await FluentActions.Invoking(() => db.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
        db.ChangeTracker.Clear();
        var existing = await db.BusinessProjects.SingleAsync(); existing.SoftDelete(user, DateTimeOffset.UtcNow.AddMinutes(1)); await db.SaveChangesAsync();
        db.ChangeTracker.Clear(); db.Add(Project(company, "GYM")); await db.SaveChangesAsync();
        (await db.BusinessProjects.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Version_is_an_optimistic_concurrency_token()
    {
        var project = Project(new(Guid.NewGuid()), "Concurrent");
        await using (var setup = Context()) { await setup.Database.MigrateAsync(); setup.Add(project); await setup.SaveChangesAsync(); }
        await using var first = Context(); await using var second = Context();
        var a = await first.BusinessProjects.SingleAsync(); var b = await second.BusinessProjects.SingleAsync();
        a.Update("A", "Gym", "Lodz", "", a.PlannedStartDate, a.PlannedOpeningDate, a.BaseCurrency, user, DateTimeOffset.UtcNow.AddMinutes(1)); await first.SaveChangesAsync();
        b.Update("B", "Gym", "Lodz", "", b.PlannedStartDate, b.PlannedOpeningDate, b.BaseCurrency, user, DateTimeOffset.UtcNow.AddMinutes(2));
        await FluentActions.Invoking(() => second.SaveChangesAsync()).Should().ThrowAsync<DbUpdateConcurrencyException>();
    }


    [Fact]
    public async Task Store_add_list_filter_get_and_active_project_queries_use_real_SQLite()
    {
        await using (var db = Context()) await db.Database.MigrateAsync();
        await using var store = Store();
        var company = new CompanyId(Guid.NewGuid());
        var project = Project(company, "Store project");
        await store.AddAsync(project, default);
        (await store.SaveChangesAsync(default)).Should().Be(BusinessProjectsSaveStatus.Success);
        (await store.ListAsync(company, BusinessProjectStatus.Draft, default)).Should().ContainSingle();
        (await store.ListAsync(company, BusinessProjectStatus.Analysis, default)).Should().BeEmpty();
        (await store.GetAsync(project.Id, false, default)).Should().NotBeNull();
        (await store.HasActiveProjectsAsync(company, default)).Should().BeTrue();
        var tracked = await store.GetAsync(project.Id, true, default);
        tracked!.SoftDelete(user, DateTimeOffset.UtcNow.AddMinutes(1));
        (await store.SaveChangesAsync(default)).Should().Be(BusinessProjectsSaveStatus.Success);
        (await store.HasActiveProjectsAsync(company, default)).Should().BeFalse();
        (await store.ListAsync(company, null, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Store_translates_duplicate_and_remains_reusable_after_result()
    {
        await using (var db = Context()) await db.Database.MigrateAsync();
        var company = new CompanyId(Guid.NewGuid());
        await using var first = Store();
        await first.AddAsync(Project(company, "Unique"), default);
        (await first.SaveChangesAsync(default)).Should().Be(BusinessProjectsSaveStatus.Success);
        await first.AddAsync(Project(company, "uNiQuE"), default);
        (await first.SaveChangesAsync(default)).Should().Be(BusinessProjectsSaveStatus.DuplicateProjectName);
        (await first.ListAsync(company, null, default)).Should().ContainSingle();
    }

    [Fact]
    public async Task Two_stores_translate_optimistic_concurrency_and_reset_for_reuse()
    {
        await using (var db = Context()) { await db.Database.MigrateAsync(); db.Add(Project(new(Guid.NewGuid()), "Concurrent store")); await db.SaveChangesAsync(); }
        await using var first = Store(); await using var second = Store();
        var id = (await first.ListAsync(new CompanyId((await Context().BusinessProjects.AsNoTracking().Select(x => x.CompanyId.Value).SingleAsync())), null, default)).Single().Id;
        var a = await first.GetAsync(id, true, default); var b = await second.GetAsync(id, true, default);
        a!.Update("First", "Gym", "Lodz", "", a.PlannedStartDate, a.PlannedOpeningDate, a.BaseCurrency, user, DateTimeOffset.UtcNow.AddMinutes(1));
        (await first.SaveChangesAsync(default)).Should().Be(BusinessProjectsSaveStatus.Success);
        b!.Update("Second", "Gym", "Lodz", "", b.PlannedStartDate, b.PlannedOpeningDate, b.BaseCurrency, user, DateTimeOffset.UtcNow.AddMinutes(2));
        (await second.SaveChangesAsync(default)).Should().Be(BusinessProjectsSaveStatus.ConcurrencyConflict);
        (await second.GetAsync(id, false, default))!.Name.Should().Be("First");
    }

    [Fact]
    public async Task Store_allows_same_name_in_another_company_and_reuse_after_archive()
    {
        await using (var db = Context()) await db.Database.MigrateAsync();
        var firstCompany = new CompanyId(Guid.NewGuid()); var secondCompany = new CompanyId(Guid.NewGuid());
        await using var store = Store();
        await store.AddAsync(Project(firstCompany, "Reusable"), default); (await store.SaveChangesAsync(default)).Should().Be(BusinessProjectsSaveStatus.Success);
        await store.AddAsync(Project(secondCompany, "reusable"), default); (await store.SaveChangesAsync(default)).Should().Be(BusinessProjectsSaveStatus.Success);
        var first = (await store.ListAsync(firstCompany, null, default)).Single(); var tracked = await store.GetAsync(first.Id, true, default);
        tracked!.SoftDelete(user, DateTimeOffset.UtcNow.AddMinutes(1)); (await store.SaveChangesAsync(default)).Should().Be(BusinessProjectsSaveStatus.Success);
        await store.AddAsync(Project(firstCompany, "REUSABLE"), default); (await store.SaveChangesAsync(default)).Should().Be(BusinessProjectsSaveStatus.Success);
        (await store.ListAsync(firstCompany, null, default)).Should().ContainSingle(); (await store.ListAsync(secondCompany, null, default)).Should().ContainSingle();
    }

    [Fact]
    public async Task Get_tracking_flag_is_executable_and_store_recovers_after_cancelled_context_creation()
    {
        await using (var db = Context()) { await db.Database.MigrateAsync(); db.Add(Project(new(Guid.NewGuid()), "Tracking")); await db.SaveChangesAsync(); }
        await using var store = Store(); var project = (await Context().BusinessProjects.AsNoTracking().SingleAsync());
        var detached = await store.GetAsync(project.Id, false, default); detached!.Update("Detached", "Gym", "Lodz", "", detached.PlannedStartDate, detached.PlannedOpeningDate, detached.BaseCurrency, user, DateTimeOffset.UtcNow.AddMinutes(1));
        await FluentActions.Invoking(() => store.SaveChangesAsync(default)).Should().ThrowAsync<BusinessProjectsPersistenceException>();
        var tracked = await store.GetAsync(project.Id, true, default); tracked!.Update("Tracked", "Gym", "Lodz", "", tracked.PlannedStartDate, tracked.PlannedOpeningDate, tracked.BaseCurrency, user, DateTimeOffset.UtcNow.AddMinutes(2));
        (await store.SaveChangesAsync(default)).Should().Be(BusinessProjectsSaveStatus.Success); (await store.GetAsync(project.Id, false, default))!.Name.Should().Be("Tracked");
        var factory = new Factory(this) { CancelNext = true }; await using var cancellingStore = new BusinessProjectsStore(factory); var cts = new CancellationTokenSource(); factory.Cancellation = cts;
        await FluentActions.Invoking(() => cancellingStore.ListAsync(project.CompanyId, null, cts.Token)).Should().ThrowAsync<OperationCanceledException>();
        (await cancellingStore.ListAsync(project.CompanyId, null, default)).Should().ContainSingle();
    }

    private BusinessProject Project(CompanyId company, string name) => BusinessProject.Create(company, name, "Gym", "Lodz", "", new(2026, 1, 1), new(2026, 2, 1), new CurrencyCode("PLN"), user, DateTimeOffset.UtcNow);
    private BusinessProjectsStore Store() => new(new Factory(this));
    private BusinessProjectsDbContext Context() => new(new DbContextOptionsBuilder<BusinessProjectsDbContext>().UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString(), sqlite => sqlite.MigrationsHistoryTable("__EFMigrationsHistory_BusinessProjects")).Options);
    private sealed class Factory(BusinessProjectsPersistenceTests owner) : IDbContextFactory<BusinessProjectsDbContext> { public bool CancelNext { get; set; } public CancellationTokenSource? Cancellation { get; set; } public BusinessProjectsDbContext CreateDbContext() => owner.Context(); public Task<BusinessProjectsDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) { if (CancelNext) { CancelNext = false; Cancellation!.Cancel(); throw new OperationCanceledException(cancellationToken); } cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(owner.Context()); } }
    public void Dispose() { File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); }
}
