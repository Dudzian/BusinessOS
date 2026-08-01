using BusinessOS.Desktop.ViewModels;
using BusinessOS.Modules.Companies.Application;
using FluentAssertions;
using Xunit;

namespace BusinessOS.UnitTests;

public sealed class CompaniesViewModelTests
{
    [Fact]
    public async Task Initial_refresh_and_successful_create_reload_list_and_preserve_message()
    {
        var service = new FakeCrudService();
        var viewModel = new CompaniesViewModel(service);
        await viewModel.RefreshAsync();
        viewModel.IsEmpty.Should().BeTrue();

        viewModel.BeginCreate();
        viewModel.LegalName = "Legal"; viewModel.DisplayName = "Display"; viewModel.TaxId = "5260250995";
        await viewModel.SaveAsync();

        viewModel.Companies.Should().ContainSingle(item => item.DisplayName == "Display");
        viewModel.IsEmpty.Should().BeFalse();
        viewModel.OperationMessage.Should().Be("Firma została utworzona.");
        service.ListCalls.Should().Be(2, "internal reload must run even while the save owns the busy state");
    }

    [Fact]
    public async Task Edit_uses_captured_identity_when_selection_changes_and_archive_reloads_empty_state()
    {
        var service = new FakeCrudService(); service.Seed("First"); service.Seed("Second");
        var viewModel = new CompaniesViewModel(service); await viewModel.RefreshAsync();
        viewModel.SelectedCompany = viewModel.Companies[0]; await viewModel.BeginEditAsync();
        viewModel.SelectedCompany = viewModel.Companies[1];
        viewModel.DisplayName = "Changed"; await viewModel.SaveAsync();
        service.UpdatedCompanyId.Should().Be(service.Items[0].Id);
        viewModel.Companies.Should().Contain(item => item.DisplayName == "Changed");

        viewModel.SelectedCompany = viewModel.Companies[0]; await viewModel.ArchiveAsync();
        viewModel.Companies.Should().ContainSingle();
        viewModel.OperationMessage.Should().Be("Firma została zarchiwizowana.");
    }

    [Fact]
    public async Task Validation_keeps_fields_and_second_save_is_blocked()
    {
        var service = new FakeCrudService { CreateGate = new TaskCompletionSource() };
        var viewModel = new CompaniesViewModel(service); viewModel.BeginCreate(); viewModel.LegalName = "Keep me";
        var first = viewModel.SaveAsync(); await service.CreateStarted.Task;
        await viewModel.SaveAsync(); service.CreateCalls.Should().Be(1); viewModel.CanSave.Should().BeFalse();
        service.CreateGate.SetResult(); await first;

        service.CreateResult = new(CompanyOperationStatus.ValidationFailed, "Popraw wskazane dane.", new Dictionary<string, string[]>(), null);
        viewModel.BeginCreate(); viewModel.LegalName = "Still here"; await viewModel.SaveAsync();
        viewModel.LegalName.Should().Be("Still here"); viewModel.IsEditorOpen.Should().BeTrue(); viewModel.CanSave.Should().BeTrue();
    }

    [Fact]
    public async Task Conflict_reloads_editor_by_captured_id_and_retry_succeeds()
    {
        var service = new FakeCrudService(); service.Seed("Original");
        var viewModel = new CompaniesViewModel(service); await viewModel.RefreshAsync();
        viewModel.SelectedCompany = viewModel.Companies[0]; await viewModel.BeginEditAsync();
        service.UpdateResults.Enqueue(new(CompanyOperationStatus.ConcurrencyConflict, "Firma została zmieniona. Odśwież dane.", new Dictionary<string, string[]>(), null));
        viewModel.DisplayName = "Mine"; await viewModel.SaveAsync();
        viewModel.IsEditorOpen.Should().BeTrue(); viewModel.DisplayName.Should().Be("Original");
        await viewModel.SaveAsync();
        viewModel.IsEditorOpen.Should().BeFalse(); viewModel.Companies.Should().ContainSingle();
    }

    [Fact]
    public async Task Service_failures_and_cancellation_are_safe()
    {
        var service = new FakeCrudService { ThrowOnList = new InvalidOperationException("Data Source=/secret/businessos.db; SQL SELECT") };
        var viewModel = new CompaniesViewModel(service); await viewModel.RefreshAsync();
        viewModel.OperationMessage.Should().NotContain("secret").And.NotContain("SQL").And.NotContain("InvalidOperationException");
        service.ThrowOnList = null; service.Seed("Company"); await viewModel.RefreshAsync(); viewModel.SelectedCompany = viewModel.Companies[0];
        service.ThrowOnGet = new InvalidOperationException("connection string"); await viewModel.BeginEditAsync();
        viewModel.OperationMessage.Should().Be("Nie udało się otworzyć danych firmy.");
    }

    [Fact]
    public async Task Recovery_is_blocked_during_refresh_editor_and_save_then_restored()
    {
        var service = new FakeCrudService { ListGate = new TaskCompletionSource() };
        var viewModel = new CompaniesViewModel(service);
        var refresh = viewModel.RefreshAsync(); await service.ListStarted.Task;
        viewModel.CanOpenRecovery.Should().BeFalse(); service.ListGate.SetResult(); await refresh;
        viewModel.CanOpenRecovery.Should().BeTrue();

        viewModel.BeginCreate(); viewModel.CanOpenRecovery.Should().BeFalse();
        service.CreateGate = new TaskCompletionSource(); var save = viewModel.SaveAsync(); await service.CreateStarted.Task;
        viewModel.CanOpenRecovery.Should().BeFalse(); service.CreateGate.SetResult(); await save;
        viewModel.CanOpenRecovery.Should().BeTrue();
    }

    [Fact]
    public async Task Recovery_is_blocked_during_archive_and_restored_after_success_or_failure()
    {
        var service = new FakeCrudService(); service.Seed("Company");
        var viewModel = new CompaniesViewModel(service); await viewModel.RefreshAsync(); viewModel.SelectedCompany = viewModel.Companies[0];
        service.ArchiveGate = new TaskCompletionSource(); var archive = viewModel.ArchiveAsync(); await service.ArchiveStarted.Task;
        viewModel.CanOpenRecovery.Should().BeFalse(); service.ArchiveGate.SetResult(); await archive;
        viewModel.CanOpenRecovery.Should().BeTrue();

        service.Seed("Failure"); await viewModel.RefreshAsync(); viewModel.SelectedCompany = viewModel.Companies[0]; service.ThrowOnArchive = new InvalidOperationException("database.db");
        await viewModel.ArchiveAsync(); viewModel.CanOpenRecovery.Should().BeTrue();
    }

    private sealed class FakeCrudService : ICompaniesCrudService
    {
        public List<CompanyDetails> Items { get; } = []; public int ListCalls; public int CreateCalls; public Guid UpdatedCompanyId;
        public Exception? ThrowOnList; public Exception? ThrowOnGet; public Exception? ThrowOnArchive;
        public TaskCompletionSource? ListGate; public TaskCompletionSource ListStarted { get; } = new();
        public TaskCompletionSource? CreateGate; public TaskCompletionSource CreateStarted { get; } = new();
        public TaskCompletionSource? ArchiveGate; public TaskCompletionSource ArchiveStarted { get; } = new();
        public CompanyOperationResult<CompanyDetails> CreateResult = default!;
        public Queue<CompanyOperationResult<CompanyDetails>> UpdateResults { get; } = new();
        public void Seed(string name) => Items.Add(Details(Guid.NewGuid(), name, 1));
        public async Task<IReadOnlyList<CompanyListItem>> ListAsync(CancellationToken token)
        {
            ListCalls++; ListStarted.TrySetResult(); if (ListGate is not null) await ListGate.Task; if (ThrowOnList is not null) throw ThrowOnList;
            return Items.Select(x => new CompanyListItem(x.Id, x.LegalName, x.DisplayName, x.TaxIdentificationNumber, x.CountryCode, x.BaseCurrency, x.Status, x.UpdatedAtUtc, x.Version)).ToArray();
        }
        public Task<CompanyDetails?> GetAsync(Guid id, CancellationToken token) { if (ThrowOnGet is not null) throw ThrowOnGet; return Task.FromResult(Items.SingleOrDefault(x => x.Id == id)); }
        public async Task<CompanyOperationResult<CompanyDetails>> CreateAsync(CreateCompanyRequest request, CancellationToken token)
        {
            CreateCalls++; CreateStarted.TrySetResult(); if (CreateGate is not null) await CreateGate.Task;
            if (CreateResult is not null) return CreateResult;
            var value = Details(Guid.NewGuid(), request.DisplayName, 1); Items.Add(value);
            return new(CompanyOperationStatus.Success, "Firma została utworzona.", new Dictionary<string, string[]>(), value);
        }
        public Task<CompanyOperationResult<CompanyDetails>> UpdateAsync(UpdateCompanyRequest request, CancellationToken token)
        {
            UpdatedCompanyId = request.CompanyId;
            if (UpdateResults.TryDequeue(out var result)) return Task.FromResult(result);
            var index = Items.FindIndex(x => x.Id == request.CompanyId); var value = Details(request.CompanyId, request.DisplayName, request.ExpectedVersion + 1); Items[index] = value;
            return Task.FromResult(new CompanyOperationResult<CompanyDetails>(CompanyOperationStatus.Success, "Zmiany zostały zapisane.", new Dictionary<string, string[]>(), value));
        }
        public async Task<CompanyOperationResult> ArchiveAsync(ArchiveCompanyRequest request, CancellationToken token) { ArchiveStarted.TrySetResult(); if (ArchiveGate is not null) await ArchiveGate.Task; if (ThrowOnArchive is not null) throw ThrowOnArchive; Items.RemoveAll(x => x.Id == request.CompanyId); return CompanyOperationResult.Success("Firma została zarchiwizowana."); }
        private static CompanyDetails Details(Guid id, string name, long version) => new(id, "Legal", name, "5260250995", "PL", "PLN", "Europe/Warsaw", CompanyStatusValue.Active, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, Guid.Empty, Guid.Empty, version);
    }
}
