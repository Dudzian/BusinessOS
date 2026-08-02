using BusinessOS.AppHost;
using BusinessOS.Modules.BusinessProjects.Infrastructure.Persistence;
using BusinessOS.Modules.Companies.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace BusinessOS.IntegrationTests;

public sealed class ApplicationStartupCoordinatorBusinessProjectsTests
{
    [Theory]
    [InlineData(false, true, true, false, true)]
    [InlineData(true, false, false, false, false)]
    [InlineData(true, true, false, true, true)]
    [InlineData(true, false, true, true, true)]
    [InlineData(true, true, true, true, true)]
    public async Task Pending_matrix_coordinates_one_backup_and_company_before_projects(bool exists, bool companiesPending, bool projectsPending, bool backupExpected, bool migrationsExpected)
    {
        var calls = new List<string>(); var inspector = new Inspector(new(exists, companiesPending ? ["c"] : []), calls); var backup = new Backup(calls); var companies = new Initializer(calls); var projects = new Projects(new(exists, projectsPending ? ["p"] : []), calls);
        var result = await Coordinator(inspector, backup, companies, projects).InitializeAsync(default);
        result.Succeeded.Should().BeTrue(); result.BackupCreated.Should().Be(backupExpected); result.MigrationsApplied.Should().Be(migrationsExpected); result.DatabaseWasCreated.Should().Be(!exists); backup.Count.Should().Be(backupExpected ? 1 : 0);
        if (migrationsExpected)
        {
            var expected = backupExpected
                ? new[] { "inspect-companies", "inspect-projects", "backup", "initialize-companies", "initialize-projects" }
                : ["inspect-companies", "inspect-projects", "initialize-companies", "initialize-projects"];
            calls.Should().ContainInOrder(expected);
        }
        else companies.Count.Should().Be(0);
    }
    [Theory]
    [InlineData("companies-inspection", ApplicationStartupFailureCode.DatabaseInspectionFailed)]
    [InlineData("projects-inspection", ApplicationStartupFailureCode.DatabaseInspectionFailed)]
    [InlineData("backup", ApplicationStartupFailureCode.BackupFailed)]
    [InlineData("companies-migration", ApplicationStartupFailureCode.MigrationFailed)]
    [InlineData("projects-migration", ApplicationStartupFailureCode.MigrationFailed)]
    public async Task Controlled_failures_stop_the_sequence_and_preserve_verified_backup(string failure, ApplicationStartupFailureCode expected)
    {
        var calls = new List<string>(); var inspector = new Inspector(new(true, ["c"]), calls) { Throw = failure == "companies-inspection" }; var backup = new Backup(calls) { Throw = failure == "backup" }; var companies = new Initializer(calls) { Throw = failure == "companies-migration" }; var projects = new Projects(new(true, ["p"]), calls) { InspectThrow = failure == "projects-inspection", InitializeThrow = failure == "projects-migration" };
        var result = await Coordinator(inspector, backup, companies, projects).InitializeAsync(default);
        result.FailureCode.Should().Be(expected); result.Succeeded.Should().BeFalse();
        if (failure == "companies-migration") projects.InitializeCount.Should().Be(0);
        if (failure == "projects-migration") result.BackupPath.Should().Be("verified.db");
    }
    [Fact] public async Task Cancellation_during_each_inspection_is_propagated() { foreach (var projectsStage in new[] { false, true }) { var token = new CancellationToken(true); var calls = new List<string>(); var inspector = new Inspector(new(true, []), calls) { Cancel = !projectsStage }; var projects = new Projects(new(true, []), calls) { InspectCancel = projectsStage }; await FluentActions.Invoking(() => Coordinator(inspector, new(calls), new(calls), projects).InitializeAsync(token)).Should().ThrowAsync<OperationCanceledException>(); } }
    [Theory]
    [InlineData(CompaniesBackupFailureCode.BackupFailed, ApplicationStartupFailureCode.BackupFailed)]
    [InlineData(CompaniesBackupFailureCode.IntegrityCheckFailed, ApplicationStartupFailureCode.BackupIntegrityCheckFailed)]
    public async Task Unsuccessful_backup_result_prevents_both_migrations(CompaniesBackupFailureCode failure, ApplicationStartupFailureCode expected)
    {
        var calls = new List<string>(); var backup = new Backup(calls) { Result = CompaniesBackupResult.Failure(failure) }; var companies = new Initializer(calls); var projects = new Projects(new(true, ["p"]), calls);
        var result = await Coordinator(new(new(true, ["c"]), calls), backup, companies, projects).InitializeAsync(default);
        result.FailureCode.Should().Be(expected); companies.Count.Should().Be(0); projects.InitializeCount.Should().Be(0); result.BackupPath.Should().BeNull();
    }
    [Theory]
    [InlineData("backup")]
    [InlineData("companies")]
    [InlineData("projects")]
    public async Task Cancellation_at_each_mutating_stage_is_propagated_and_stops_following_stages(string stage)
    {
        var calls = new List<string>(); var cts = new CancellationTokenSource(); var backup = new Backup(calls) { Cancel = stage == "backup", Cancellation = cts }; var companies = new Initializer(calls) { Cancel = stage == "companies", Cancellation = cts }; var projects = new Projects(new(true, ["p"]), calls) { InitializeCancel = stage == "projects", Cancellation = cts };
        await FluentActions.Invoking(() => Coordinator(new(new(true, ["c"]), calls), backup, companies, projects).InitializeAsync(cts.Token)).Should().ThrowAsync<OperationCanceledException>();
        if (stage is "backup" or "companies") projects.InitializeCount.Should().Be(0);
    }
    [Fact]
    public async Task Initialization_semaphore_prevents_overlapping_inspections_without_delays()
    {
        var calls = new List<string>(); var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var inspector = new Inspector(new(true, []), calls) { Gate = gate, Entered = entered };
        var coordinator = Coordinator(inspector, new(calls), new(calls), new(new(true, []), calls)); var first = coordinator.InitializeAsync(default); await entered.Task; var second = coordinator.InitializeAsync(default);
        inspector.Count.Should().Be(1); gate.SetResult(); await Task.WhenAll(first, second); inspector.Count.Should().Be(2);
    }
    private static ApplicationStartupCoordinator Coordinator(Inspector inspector, Backup backup, Initializer initializer, Projects projects) => new(inspector, backup, initializer, NullLogger<ApplicationStartupCoordinator>.Instance, projects);
    private sealed class Inspector(CompaniesMigrationState state, List<string> calls) : ICompaniesMigrationInspector { public bool Throw, Cancel; public int Count; public TaskCompletionSource? Gate, Entered; public async Task<CompaniesMigrationState> InspectAsync(CancellationToken ct) { calls.Add("inspect-companies"); Count++; Entered?.TrySetResult(); if (Gate is not null) await Gate.Task.WaitAsync(ct); if (Cancel) throw new OperationCanceledException(ct); if (Throw) throw new InvalidOperationException(); return state; } }
    private sealed class Backup(List<string> calls) : ICompaniesDatabaseBackupService { public bool Throw, Cancel; public CancellationTokenSource? Cancellation; public CompaniesBackupResult Result = CompaniesBackupResult.Success("verified.db"); public int Count; public Task<CompaniesBackupResult> CreateBackupAsync(CancellationToken ct) { calls.Add("backup"); Count++; if (Cancel) { Cancellation!.Cancel(); throw new OperationCanceledException(ct); } if (Throw) throw new InvalidOperationException(); return Task.FromResult(Result); } }
    private sealed class Initializer(List<string> calls) : ICompaniesDatabaseInitializer { public bool Throw, Cancel; public CancellationTokenSource? Cancellation; public int Count; public Task InitializeAsync(CancellationToken ct) { calls.Add("initialize-companies"); Count++; if (Cancel) { Cancellation!.Cancel(); throw new OperationCanceledException(ct); } if (Throw) throw new InvalidOperationException(); return Task.CompletedTask; } }
    private sealed class Projects(BusinessProjectsMigrationState state, List<string> calls) : IBusinessProjectsDatabaseLifecycle { public bool InspectThrow, InspectCancel, InitializeThrow, InitializeCancel; public CancellationTokenSource? Cancellation; public int InitializeCount; public Task<BusinessProjectsMigrationState> InspectAsync(CancellationToken ct) { calls.Add("inspect-projects"); if (InspectCancel) throw new OperationCanceledException(ct); if (InspectThrow) throw new InvalidOperationException(); return Task.FromResult(state); } public Task InitializeAsync(CancellationToken ct) { calls.Add("initialize-projects"); InitializeCount++; if (InitializeCancel) { Cancellation!.Cancel(); throw new OperationCanceledException(ct); } if (InitializeThrow) throw new InvalidOperationException(); return Task.CompletedTask; } }
}
