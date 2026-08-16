using System.Globalization;
using BusinessOS.Desktop.ViewModels;
using BusinessOS.Modules.Budgeting.Application;
using Xunit;

namespace BusinessOS.UnitTests;

public sealed class SupplierInvoicesViewModelTests
{
    private static readonly BudgetProjectInfo P1 = new(Guid.Parse("10000000-0000-0000-0000-000000000001"), "One", "PLN", true);
    private static readonly BudgetProjectInfo P2 = new(Guid.Parse("20000000-0000-0000-0000-000000000002"), "Two", "EUR", true);
    private static SupplierInvoiceItem Item(Guid? id = null, Guid? project = null, string number = "INV-1", decimal amount = 10, long version = 1) => new(id ?? Guid.NewGuid(), project ?? P1.Id, "Acme", number, amount, project == P2.Id ? "EUR" : "PLN", new(2026, 1, 10), new(2026, 2, 10), "note", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, version);

    [Fact]
    public async Task Reload_success_failure_deep_rollback_and_recovery_are_atomic()
    {
        var item = Item(); var service = new FakeService { Items = [item] }; var lookup = new FakeLookup { Items = [P1, P2] }; var vm = Vm(service, lookup);
        await vm.ReloadProjectsAsync(); await vm.SelectInvoiceAsync(item); Assert.True(vm.LastProjectsReloadSucceeded); Assert.Equal("", vm.OperationMessage);
        var projects = vm.Projects.ToArray(); var invoices = vm.Invoices.ToArray(); lookup.Failure = true; await vm.ReloadProjectsAsync();
        Assert.False(vm.LastProjectsReloadSucceeded); Assert.Equal(projects, vm.Projects); Assert.Equal(P1, vm.SelectedProject); Assert.Equal(invoices, vm.Invoices); Assert.Equal(item.Id, vm.SelectedInvoice!.Id); Assert.Equal("PLN", vm.ProjectCurrency); Assert.Equal(10, vm.InvoiceTotal); Assert.NotEmpty(vm.OperationMessage);
        lookup.Failure = false; await vm.ReloadProjectsAsync(); Assert.True(vm.LastProjectsReloadSucceeded); Assert.Empty(vm.OperationMessage);
    }

    [Fact]
    public async Task Project_selection_canonicalizes_rolls_back_deeply_and_ignores_foreign()
    {
        var old = Item(); var service = new FakeService { Items = [old] }; var lookup = new FakeLookup { Items = [P1, P2] }; var vm = Vm(service, lookup); await vm.ReloadProjectsAsync(); await vm.SelectInvoiceAsync(old);
        service.Failure = true; var changed = new List<string?>(); vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName); await vm.SelectProjectAsync(P2 with { Name = "foreign instance" });
        Assert.Equal(P1, vm.SelectedProject); Assert.Equal(old.Id, vm.SelectedInvoice!.Id); Assert.Equal(10, vm.InvoiceTotal); Assert.Contains(nameof(vm.SelectedProject), changed);
        service.Failure = false; var calls = service.ListCalls; await vm.SelectProjectAsync(new(Guid.NewGuid(), "Foreign", "USD", true)); Assert.Equal(calls, service.ListCalls); Assert.Equal(P1, vm.SelectedProject);
        service.Items = [Item(project: P2.Id)]; await vm.SelectProjectAsync(P2 with { Name = "copy" }); Assert.Same(vm.Projects[1], vm.SelectedProject);
    }

    [Fact]
    public async Task Invoice_selection_canonicalizes_and_ignores_foreign()
    {
        var item = Item(); var vm = Vm(new() { Items = [item] }, new() { Items = [P1] }); await vm.ReloadProjectsAsync();
        await vm.SelectInvoiceAsync(item with { SupplierName = "copy" }); Assert.Same(vm.Invoices[0], vm.SelectedInvoice);
        await vm.SelectInvoiceAsync(Item()); Assert.Same(vm.Invoices[0], vm.SelectedInvoice);
    }

    [Fact]
    public async Task Refresh_preserves_or_clears_selection_rolls_back_and_does_not_own_reload_flag()
    {
        var item = Item(); var service = new FakeService { Items = [item] }; var vm = Vm(service, new() { Items = [P1] }); await vm.ReloadProjectsAsync(); await vm.SelectInvoiceAsync(item);
        service.Items = [item with { Amount = 20 }]; await vm.RefreshAsync(); Assert.Equal(item.Id, vm.SelectedInvoice!.Id); Assert.Equal(20, vm.InvoiceTotal); Assert.True(vm.LastProjectsReloadSucceeded);
        service.Failure = true; await vm.RefreshAsync(); Assert.Equal(20, vm.InvoiceTotal); Assert.Equal(item.Id, vm.SelectedInvoice!.Id); Assert.True(vm.LastProjectsReloadSucceeded); Assert.NotEmpty(vm.OperationMessage);
        service.Failure = false; service.Items = []; await vm.RefreshAsync(); Assert.Null(vm.SelectedInvoice); Assert.Empty(vm.OperationMessage); Assert.True(vm.LastProjectsReloadSucceeded);
    }

    [Fact]
    public async Task Begin_add_uses_local_time_defaults_and_publishes_all_fields()
    {
        var vm = Vm(new(), new() { Items = [P1] }, new FixedTimeProvider(new DateTimeOffset(2026, 3, 4, 12, 0, 0, TimeSpan.FromHours(2)))); await vm.ReloadProjectsAsync(); var changed = new List<string?>(); vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName); vm.BeginAddInvoice();
        Assert.Equal(new DateOnly(2026, 3, 4), vm.InvoiceDate); Assert.Equal(new DateOnly(2026, 4, 3), vm.DueDate); Assert.Equal(("", "", "", ""), (vm.SupplierName, vm.InvoiceNumber, vm.Amount, vm.Note)); AssertEditorFields(changed); Assert.True(vm.IsEditorOpen);
    }

    [Fact]
    public async Task Begin_edit_copies_fields_and_save_update_uses_captured_identity_version()
    {
        var item = Item(amount: 120.5m, version: 7); var service = new FakeService { Items = [item] }; var vm = Vm(service, new() { Items = [P1] }); await vm.ReloadProjectsAsync(); await vm.SelectInvoiceAsync(item); var changed = new List<string?>(); vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName); vm.BeginEditInvoice();
        Assert.Equal(("Acme", "INV-1", "120.5", new DateOnly(2026, 1, 10), new DateOnly(2026, 2, 10), "note"), (vm.SupplierName, vm.InvoiceNumber, vm.Amount, vm.InvoiceDate, vm.DueDate, vm.Note)); AssertEditorFields(changed);
        service.UpdateValue = item with { Amount = 135, Version = 8 }; service.Items = [service.UpdateValue]; vm.Amount = "135"; await vm.SaveInvoiceAsync(); Assert.Equal((item.Id, 7L), service.Updated); Assert.False(vm.IsEditorOpen); Assert.Equal(135, vm.InvoiceTotal); Assert.Equal(item.Id, vm.SelectedInvoice!.Id);
    }

    [Fact]
    public async Task Save_create_success_invalid_decimal_and_controlled_statuses()
    {
        var service = new FakeService(); var vm = Vm(service, new() { Items = [P1] }); await vm.ReloadProjectsAsync(); vm.BeginAddInvoice(); vm.Amount = "not-decimal"; await vm.SaveInvoiceAsync(); Assert.True(vm.IsEditorOpen); Assert.NotEmpty(vm.OperationMessage); Assert.Equal(0, service.CreateCalls);
        foreach (var status in new[] { SupplierInvoiceOperationStatus.ValidationFailure, SupplierInvoiceOperationStatus.DuplicateInvoice, SupplierInvoiceOperationStatus.ConcurrencyConflict, SupplierInvoiceOperationStatus.ProjectUnavailable, SupplierInvoiceOperationStatus.PersistenceFailure, SupplierInvoiceOperationStatus.Cancelled })
        { service.ResultStatus = status; vm.Amount = "1"; await vm.SaveInvoiceAsync(); Assert.True(vm.IsEditorOpen); Assert.Equal("safe", vm.OperationMessage); }
        var saved = Item(amount: 25); service.ResultStatus = SupplierInvoiceOperationStatus.Success; service.CreateValue = saved; service.Items = [saved]; await vm.SaveInvoiceAsync(); Assert.False(vm.IsEditorOpen); Assert.Equal(saved.Id, vm.SelectedInvoice!.Id); Assert.Equal(25, vm.InvoiceTotal);
    }

    [Fact]
    public async Task Pending_read_and_save_gate_every_capability_without_delays()
    {
        var service = new FakeService { Items = [Item()] }; var vm = Vm(service, new() { Items = [P1] }); await vm.ReloadProjectsAsync(); service.ListGate = new(TaskCreationOptions.RunContinuationsAsynchronously); var refresh = vm.RefreshAsync(); AssertAllBlocked(vm); service.ListGate.SetResult(service.Items); await refresh; Assert.True(vm.CanNavigate);
        vm.BeginAddInvoice(); vm.Amount = "1"; service.CreateGate = new(TaskCreationOptions.RunContinuationsAsynchronously); var save = vm.SaveInvoiceAsync(); AssertAllBlocked(vm); service.CreateGate.SetResult(new(SupplierInvoiceOperationStatus.ValidationFailure, "safe", null)); await save; Assert.True(vm.CanSaveInvoice); Assert.True(vm.CanCancelEditor);
    }

    [Fact]
    public async Task Archive_captures_exact_target_cancel_and_all_outcomes_clear_state()
    {
        var a = Item(number: "A", version: 4); var b = Item(number: "B", version: 8); var service = new FakeService { Items = [a, b] }; var vm = Vm(service, new() { Items = [P1] }); await vm.ReloadProjectsAsync(); await vm.SelectInvoiceAsync(a); vm.BeginArchiveInvoice(); Assert.Equal(("Acme", "A"), (vm.ArchiveSupplierName, vm.ArchiveInvoiceNumber)); vm.CancelArchive(); AssertArchiveCleared(vm);
        foreach (var status in new[] { SupplierInvoiceOperationStatus.NotFound, SupplierInvoiceOperationStatus.Archived, SupplierInvoiceOperationStatus.ConcurrencyConflict, SupplierInvoiceOperationStatus.ProjectUnavailable, SupplierInvoiceOperationStatus.PersistenceFailure, SupplierInvoiceOperationStatus.Cancelled })
        { await vm.SelectInvoiceAsync(a); vm.BeginArchiveInvoice(); service.ArchiveStatus = status; await vm.ConfirmArchiveAsync(); Assert.Equal((a.Id, 4L), service.Archived); AssertArchiveCleared(vm); }
        await vm.SelectInvoiceAsync(a); vm.BeginArchiveInvoice(); service.ArchiveStatus = SupplierInvoiceOperationStatus.Success; service.Items = [b]; await vm.ConfirmArchiveAsync(); AssertArchiveCleared(vm); Assert.Single(vm.Invoices); Assert.Equal(b.Amount, vm.InvoiceTotal);
    }

    [Fact]
    public async Task Posting_captures_target_blocks_navigation_and_success_selects_canonical_posted_row()
    {
        var a = Item(number: "A", amount: 120.5m, version: 7); var b = Item(number: "B", version: 2); var service = new FakeService { Items = [a, b] }; var posting = new FakePosting(); var vm = new SupplierInvoicesViewModel(service, posting, new FakeLookup { Items = [P1] }, TimeProvider.System); await vm.ReloadProjectsAsync(); await vm.SelectInvoiceAsync(a); Assert.True(vm.CanPostInvoice); vm.BeginPostInvoice(); Assert.True(vm.IsPostingDialogOpen); Assert.Null(vm.PostingKind); Assert.Equal(("Acme", "A", "120.5", "PLN", "2026-01-10"), (vm.PostingSupplierName, vm.PostingInvoiceNumber, vm.PostingAmount, vm.PostingCurrency, vm.PostingInvoiceDate)); Assert.False(vm.CanNavigate);
        await vm.SelectInvoiceAsync(b); vm.PostingKind = BusinessOS.Modules.Budgeting.Domain.ActualCostKind.Capex; service.Items = [a with { PostedActualCostId = Guid.NewGuid(), PostedAtUtc = DateTimeOffset.UtcNow, Version = 8 }, b]; await vm.ConfirmPostingAsync(); Assert.Equal((a.Id, 7L), posting.Target); Assert.False(vm.IsPostingDialogOpen); Assert.True(vm.SelectedInvoice!.IsPosted); Assert.Equal(a.Id, vm.SelectedInvoice.Id); Assert.True(vm.CanNavigate);
    }

    [Fact]
    public async Task Posting_requires_kind_cancel_clears_and_posted_capabilities_are_disabled()
    {
        var pending = Item(); var posted = pending with { PostedActualCostId = Guid.NewGuid(), PostedAtUtc = DateTimeOffset.UtcNow }; var posting = new FakePosting(); var vm = new SupplierInvoicesViewModel(new FakeService { Items = [pending] }, posting, new FakeLookup { Items = [P1] }, TimeProvider.System); await vm.ReloadProjectsAsync(); await vm.SelectInvoiceAsync(pending); vm.BeginPostInvoice(); await vm.ConfirmPostingAsync(); Assert.Equal(0, posting.Calls); Assert.Equal("Wybierz rodzaj kosztu.", vm.OperationMessage); Assert.True(vm.IsPostingDialogOpen); vm.CancelPosting(); Assert.False(vm.IsPostingDialogOpen); Assert.Equal("", vm.PostingSupplierName); Assert.Null(vm.PostingKind);
        var service = new FakeService { Items = [posted] }; vm = new(service, posting, new FakeLookup { Items = [P1] }, TimeProvider.System); await vm.ReloadProjectsAsync(); await vm.SelectInvoiceAsync(posted); Assert.False(vm.CanPostInvoice); Assert.False(vm.CanEditInvoice); Assert.False(vm.CanArchiveInvoice);
    }

    [Fact]
    public async Task Posting_invoice_date_is_invariant_iso_and_clears_with_capture()
    {
        var original = CultureInfo.CurrentCulture; var originalUi = CultureInfo.CurrentUICulture;
        try { CultureInfo.CurrentCulture = new("en-US"); CultureInfo.CurrentUICulture = new("en-US"); var item = Item(); var vm = Vm(new() { Items = [item] }, new() { Items = [P1] }); await vm.ReloadProjectsAsync(); await vm.SelectInvoiceAsync(item); vm.BeginPostInvoice(); Assert.Equal("2026-01-10", vm.PostingInvoiceDate); vm.CancelPosting(); Assert.Equal(string.Empty, vm.PostingInvoiceDate); }
        finally { CultureInfo.CurrentCulture = original; CultureInfo.CurrentUICulture = originalUi; }
    }

    [Fact]
    public async Task Posting_statuses_and_busy_gate_follow_dialog_policy_without_delays()
    {
        foreach (var status in Enum.GetValues<SupplierInvoicePostingStatus>())
        {
            var item = Item(); var service = new FakeService { Items = [item] }; var posting = new FakePosting { Status = status }; var vm = new SupplierInvoicesViewModel(service, posting, new FakeLookup { Items = [P1] }, TimeProvider.System); await vm.ReloadProjectsAsync(); await vm.SelectInvoiceAsync(item); vm.BeginPostInvoice(); vm.PostingKind = BusinessOS.Modules.Budgeting.Domain.ActualCostKind.Opex;
            var callsBeforeConfirm = service.ListCalls;
            if (status == SupplierInvoicePostingStatus.Success) service.Items = [item with { PostedActualCostId = Guid.NewGuid(), PostedAtUtc = DateTimeOffset.UtcNow, Version = 2 }];
            await vm.ConfirmPostingAsync(); Assert.Equal("safe", vm.OperationMessage);
            if (status is SupplierInvoicePostingStatus.ValidationFailure or SupplierInvoicePostingStatus.PersistenceFailure)
            {
                Assert.True(vm.IsPostingDialogOpen); Assert.Equal(BusinessOS.Modules.Budgeting.Domain.ActualCostKind.Opex, vm.PostingKind); Assert.Equal(("Acme", "INV-1", "10", "PLN", "2026-01-10"), (vm.PostingSupplierName, vm.PostingInvoiceNumber, vm.PostingAmount, vm.PostingCurrency, vm.PostingInvoiceDate)); Assert.False(vm.CanNavigate); Assert.True(vm.CanConfirmPosting); Assert.True(vm.CanCancelPosting); Assert.Equal(callsBeforeConfirm, service.ListCalls);
            }
            else
            {
                Assert.False(vm.IsPostingDialogOpen); Assert.Null(vm.PostingKind); Assert.Equal(("", "", "", "", ""), (vm.PostingSupplierName, vm.PostingInvoiceNumber, vm.PostingAmount, vm.PostingCurrency, vm.PostingInvoiceDate)); Assert.True(vm.CanNavigate);
                Assert.Equal(status == SupplierInvoicePostingStatus.Cancelled ? callsBeforeConfirm : callsBeforeConfirm + 1, service.ListCalls);
            }
        }
        var source = Item(); var gatedService = new FakeService { Items = [source] }; var gated = new FakePosting { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) }; var busy = new SupplierInvoicesViewModel(gatedService, gated, new FakeLookup { Items = [P1] }, TimeProvider.System); await busy.ReloadProjectsAsync(); await busy.SelectInvoiceAsync(source); busy.BeginPostInvoice(); busy.PostingKind = BusinessOS.Modules.Budgeting.Domain.ActualCostKind.Capex; var task = busy.ConfirmPostingAsync(); Assert.True(busy.IsBusy); Assert.False(busy.CanNavigate); Assert.False(busy.CanPostInvoice); Assert.False(busy.CanConfirmPosting); Assert.False(busy.CanCancelPosting); gated.Gate.SetResult(new(SupplierInvoicePostingStatus.PersistenceFailure, "safe", null)); await task; Assert.True(busy.IsPostingDialogOpen); Assert.False(busy.CanNavigate); Assert.True(busy.CanConfirmPosting); Assert.True(busy.CanCancelPosting);
    }

    private static void AssertAllBlocked(SupplierInvoicesViewModel vm) { Assert.True(vm.IsBusy); Assert.False(vm.CanNavigate); Assert.False(vm.CanSelectProject); Assert.False(vm.CanSelectInvoice); Assert.False(vm.CanRefresh); Assert.False(vm.CanAddInvoice); Assert.False(vm.CanEditInvoice); Assert.False(vm.CanArchiveInvoice); Assert.False(vm.CanSaveInvoice); Assert.False(vm.CanCancelEditor); }
    private static void AssertArchiveCleared(SupplierInvoicesViewModel vm) { Assert.False(vm.IsArchiveDialogOpen); Assert.Null(vm.ArchiveSupplierName); Assert.Null(vm.ArchiveInvoiceNumber); Assert.True(vm.CanNavigate); }
    private static void AssertEditorFields(IEnumerable<string?> fields) { foreach (var n in new[] { "SupplierName", "InvoiceNumber", "Amount", "InvoiceDate", "DueDate", "Note" }) Assert.Contains(n, fields); }
    private static SupplierInvoicesViewModel Vm(FakeService service, FakeLookup lookup, TimeProvider? time = null) => new(service, new FakePosting(), lookup, time ?? TimeProvider.System);


    private sealed class FakePosting : ISupplierInvoicePostingService
    {
        public SupplierInvoicePostingStatus Status = SupplierInvoicePostingStatus.Success; public int Calls; public (Guid, long)? Target; public TaskCompletionSource<SupplierInvoicePostingResult<SupplierInvoicePostingReceipt>>? Gate;
        public Task<SupplierInvoicePostingResult<SupplierInvoicePostingReceipt>> PostAsync(Guid id, long version, BusinessOS.Modules.Budgeting.Domain.ActualCostKind kind, CancellationToken ct = default) { Calls++; Target = (id, version); return Gate?.Task ?? Task.FromResult(new SupplierInvoicePostingResult<SupplierInvoicePostingReceipt>(Status, "safe", null)); }
    }
    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider { public override DateTimeOffset GetUtcNow() => value.ToUniversalTime(); public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("fixed", value.Offset, "fixed", "fixed"); }
    private sealed class FakeLookup : IBudgetingProjectLookup
    {
        public IReadOnlyList<BudgetProjectInfo> Items = []; public bool Failure;
        public Task<BudgetProjectInfo?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.SingleOrDefault(x => x.Id == id));
        public Task<IReadOnlyList<BudgetProjectInfo>> ListAvailableAsync(CancellationToken ct) => Failure ? Task.FromException<IReadOnlyList<BudgetProjectInfo>>(new BudgetingProjectLookupException("technical", new Exception())) : Task.FromResult(Items);
    }
    private sealed class FakeService : ISupplierInvoicesCrudService
    {
        public IReadOnlyList<SupplierInvoiceItem> Items = []; public bool Failure; public int ListCalls; public int CreateCalls; public TaskCompletionSource<IReadOnlyList<SupplierInvoiceItem>>? ListGate; public TaskCompletionSource<SupplierInvoiceResult<SupplierInvoiceItem>>? CreateGate; public SupplierInvoiceOperationStatus ResultStatus = SupplierInvoiceOperationStatus.Success; public SupplierInvoiceOperationStatus ArchiveStatus = SupplierInvoiceOperationStatus.Success; public SupplierInvoiceItem? CreateValue; public SupplierInvoiceItem? UpdateValue; public (Guid, long)? Updated; public (Guid, long)? Archived;
        public Task<IReadOnlyList<SupplierInvoiceItem>> ListAsync(Guid id, CancellationToken ct = default) { ListCalls++; return Failure ? Task.FromException<IReadOnlyList<SupplierInvoiceItem>>(new SupplierInvoicesReadException(new Exception())) : ListGate?.Task ?? Task.FromResult(Items.Where(x => x.ProjectId == id).ToArray() as IReadOnlyList<SupplierInvoiceItem>); }
        public Task<SupplierInvoiceItem?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Items.SingleOrDefault(x => x.Id == id));
        public Task<SupplierInvoiceResult<SupplierInvoiceItem>> CreateAsync(Guid p, string s, string n, decimal a, string c, DateOnly i, DateOnly d, string? note, CancellationToken ct = default) { CreateCalls++; return CreateGate?.Task ?? Task.FromResult(new SupplierInvoiceResult<SupplierInvoiceItem>(ResultStatus, "safe", ResultStatus == SupplierInvoiceOperationStatus.Success ? CreateValue : null)); }
        public Task<SupplierInvoiceResult<SupplierInvoiceItem>> UpdateAsync(Guid id, long v, string s, string n, decimal a, string c, DateOnly i, DateOnly d, string? note, CancellationToken ct = default) { Updated = (id, v); return Task.FromResult(new SupplierInvoiceResult<SupplierInvoiceItem>(ResultStatus, "safe", ResultStatus == SupplierInvoiceOperationStatus.Success ? UpdateValue : null)); }
        public Task<SupplierInvoiceResult> ArchiveAsync(Guid id, long v, CancellationToken ct = default) { Archived = (id, v); return Task.FromResult(new SupplierInvoiceResult(ArchiveStatus, "safe")); }
    }
}
