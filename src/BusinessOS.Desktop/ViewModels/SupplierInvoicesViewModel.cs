using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using BusinessOS.Modules.Budgeting.Application;
using BusinessOS.Modules.Budgeting.Domain;

namespace BusinessOS.Desktop.ViewModels;

public sealed class SupplierInvoicesViewModel(ISupplierInvoicesCrudService service, ISupplierInvoicePostingService posting, IBudgetingProjectLookup projects, TimeProvider clock) : INotifyPropertyChanged
{
    private BudgetProjectInfo? selectedProject; private SupplierInvoiceItem? selectedInvoice; private bool busy; private Guid? editingId; private long editingVersion; private ArchiveTarget? archiveTarget; private PostingTarget? postingTarget; private ActualCostKind? postingKind;
    public ObservableCollection<BudgetProjectInfo> Projects { get; } = []; public ObservableCollection<SupplierInvoiceItem> Invoices { get; } = [];
    public BudgetProjectInfo? SelectedProject => selectedProject; public SupplierInvoiceItem? SelectedInvoice => selectedInvoice; public string ProjectCurrency => selectedProject?.BaseCurrency ?? string.Empty;
    public string SupplierName { get; set; } = string.Empty; public string InvoiceNumber { get; set; } = string.Empty; public string Amount { get; set; } = string.Empty; public DateOnly InvoiceDate { get; set; }
    public DateOnly DueDate { get; set; }
    public string Note { get; set; } = string.Empty;
    public bool IsBusy { get => busy; private set { busy = value; Notify(); } }
    public bool IsEditorOpen { get; private set; }
    public bool IsArchiveDialogOpen { get; private set; }
    public bool IsPostingDialogOpen { get; private set; }
    public IReadOnlyList<ActualCostKind> PostingKinds { get; } = [ActualCostKind.Capex, ActualCostKind.Opex];
    public ActualCostKind? PostingKind { get => postingKind; set { postingKind = value; Notify(); } }
    public string PostingSupplierName => postingTarget?.Supplier ?? string.Empty;
    public string PostingInvoiceNumber => postingTarget?.Number ?? string.Empty;
    public string PostingAmount => postingTarget?.Amount.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty;
    public string PostingCurrency => postingTarget?.Currency ?? string.Empty;
    public string PostingInvoiceDate => postingTarget is null ? string.Empty : postingTarget.InvoiceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public bool LastProjectsReloadSucceeded { get; private set; }
    public string OperationMessage { get; private set; } = string.Empty;
    public decimal InvoiceTotal => Invoices.Sum(x => x.Amount); public bool CanNavigate => !IsBusy && !IsEditorOpen && !IsArchiveDialogOpen && !IsPostingDialogOpen; public bool CanSelectProject => CanNavigate; public bool CanSelectInvoice => CanNavigate && selectedProject is not null; public bool CanRefresh => CanNavigate && selectedProject is not null; public bool CanAddInvoice => CanRefresh; public bool CanEditInvoice => CanRefresh && selectedInvoice is { IsPosted: false }; public bool CanArchiveInvoice => CanEditInvoice; public bool CanPostInvoice => CanRefresh && selectedInvoice is { IsPosted: false }; public bool CanConfirmPosting => IsPostingDialogOpen && PostingKind is not null && !IsBusy; public bool CanCancelPosting => IsPostingDialogOpen && !IsBusy; public bool CanSaveInvoice => IsEditorOpen && !IsBusy; public bool CanCancelEditor => IsEditorOpen && !IsBusy;
    public string? ArchiveSupplierName => archiveTarget?.Supplier; public string? ArchiveInvoiceNumber => archiveTarget?.Number;
    public event PropertyChangedEventHandler? PropertyChanged;
    public async Task ReloadProjectsAsync(CancellationToken ct = default) { LastProjectsReloadSucceeded = false; Notify(); await Run(async () => { var candidate = await projects.ListAvailableAsync(ct); var project = selectedProject is null ? candidate.FirstOrDefault() : candidate.SingleOrDefault(x => x.Id == selectedProject.Id) ?? candidate.FirstOrDefault(); var rows = project is null ? [] : await service.ListAsync(project.Id, ct); Projects.Clear(); foreach (var p in candidate) Projects.Add(p); Commit(project, rows); LastProjectsReloadSucceeded = true; OperationMessage = string.Empty; }, ct, true); }
    public async Task SelectProjectAsync(BudgetProjectInfo project, CancellationToken ct = default) { if (!CanSelectProject) return; var canonical = Projects.SingleOrDefault(x => x.Id == project.Id); if (canonical is null) return; await Run(async () => Commit(canonical, await service.ListAsync(canonical.Id, ct)), ct, true); }
    public Task SelectInvoiceAsync(SupplierInvoiceItem invoice) { if (CanSelectInvoice) { var canonical = Invoices.SingleOrDefault(x => x.Id == invoice.Id); if (canonical is not null) { selectedInvoice = canonical; Notify(); } } return Task.CompletedTask; }
    public async Task RefreshAsync(CancellationToken ct = default) { if (!CanRefresh) return; var project = selectedProject!; await Run(async () => { var rows = await service.ListAsync(project.Id, ct); Commit(project, rows, selectedInvoice?.Id); OperationMessage = string.Empty; }, ct, true); }
    public void BeginAddInvoice() { if (!CanAddInvoice) return; editingId = null; editingVersion = 0; SupplierName = InvoiceNumber = Amount = Note = string.Empty; InvoiceDate = DateOnly.FromDateTime(clock.GetLocalNow().DateTime); DueDate = InvoiceDate.AddDays(30); EditorChanged(); IsEditorOpen = true; Notify(); }
    public void BeginEditInvoice() { if (!CanEditInvoice) return; var x = selectedInvoice!; editingId = x.Id; editingVersion = x.Version; SupplierName = x.SupplierName; InvoiceNumber = x.InvoiceNumber; Amount = x.Amount.ToString(CultureInfo.InvariantCulture); InvoiceDate = x.InvoiceDate; DueDate = x.DueDate; Note = x.Note ?? string.Empty; EditorChanged(); IsEditorOpen = true; Notify(); }
    public void CancelEditor() { if (!CanCancelEditor) return; editingId = null; editingVersion = 0; IsEditorOpen = false; Notify(); }
    public async Task SaveInvoiceAsync(CancellationToken ct = default) { if (!CanSaveInvoice || !decimal.TryParse(Amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)) { OperationMessage = "Popraw wskazane dane."; Notify(); return; } await Run(async () => { var r = editingId is null ? await service.CreateAsync(selectedProject!.Id, SupplierName, InvoiceNumber, amount, selectedProject.BaseCurrency, InvoiceDate, DueDate, Note, ct) : await service.UpdateAsync(editingId.Value, editingVersion, SupplierName, InvoiceNumber, amount, selectedProject!.BaseCurrency, InvoiceDate, DueDate, Note, ct); OperationMessage = r.SafeMessage; if (r.Status == SupplierInvoiceOperationStatus.Success) { IsEditorOpen = false; var rows = await service.ListAsync(selectedProject.Id, ct); Commit(selectedProject, rows, r.Value?.Id); editingId = null; } }, ct); }
    public void BeginArchiveInvoice() { if (!CanArchiveInvoice) return; archiveTarget = new(selectedInvoice!.Id, selectedInvoice.Version, selectedInvoice.SupplierName, selectedInvoice.InvoiceNumber); IsArchiveDialogOpen = true; Notify(); }
    public void CancelArchive() { if (IsBusy) return; ClearArchive(); }
    public async Task ConfirmArchiveAsync(CancellationToken ct = default) { if (!IsArchiveDialogOpen || archiveTarget is null || IsBusy) return; var target = archiveTarget; await Run(async () => { var r = await service.ArchiveAsync(target.Id, target.Version, ct); OperationMessage = r.SafeMessage; ClearArchive(); if (r.Status == SupplierInvoiceOperationStatus.Success) Commit(selectedProject, await service.ListAsync(selectedProject!.Id, ct)); }, ct, false, ClearArchive); }
    public void BeginPostInvoice() { if (!CanPostInvoice) return; var x = selectedInvoice!; postingTarget = new(x.Id, x.Version, x.SupplierName, x.InvoiceNumber, x.Amount, x.Currency, x.InvoiceDate); PostingKind = null; IsPostingDialogOpen = true; Notify(); }
    public void CancelPosting() { if (!CanCancelPosting) return; ClearPosting(); }
    public async Task ConfirmPostingAsync(CancellationToken ct = default)
    {
        if (!IsPostingDialogOpen || postingTarget is null || IsBusy) return;
        if (PostingKind is null) { OperationMessage = "Wybierz rodzaj kosztu."; Notify(); return; }
        var target = postingTarget; var kind = PostingKind.Value;
        await Run(async () =>
        {
            var result = await posting.PostAsync(target.Id, target.Version, kind, ct); OperationMessage = result.SafeMessage;
            if (result.Status is SupplierInvoicePostingStatus.ValidationFailure or SupplierInvoicePostingStatus.PersistenceFailure) return;
            ClearPosting();
            if (result.Status != SupplierInvoicePostingStatus.Cancelled && selectedProject is not null)
                Commit(selectedProject, await service.ListAsync(selectedProject.Id, ct), target.Id);
        }, ct);
    }
    private async Task Run(Func<Task> action, CancellationToken ct, bool rollbackSelection = false, Action? finallyAction = null) { IsBusy = true; try { await action(); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { OperationMessage = "Operacja została anulowana."; } catch (Exception e) when (e is SupplierInvoicesReadException or BudgetingProjectLookupException) { OperationMessage = "Nie udało się odczytać faktur."; if (rollbackSelection) Changed(nameof(SelectedProject)); } finally { finallyAction?.Invoke(); IsBusy = false; Notify(); } }
    private void Commit(BudgetProjectInfo? project, IReadOnlyList<SupplierInvoiceItem> rows, Guid? select = null) { selectedProject = project; Invoices.Clear(); foreach (var x in rows) Invoices.Add(x); selectedInvoice = select.HasValue ? Invoices.SingleOrDefault(x => x.Id == select) : null; Notify(); }
    private void ClearArchive() { archiveTarget = null; IsArchiveDialogOpen = false; Notify(); }
    private void ClearPosting() { postingTarget = null; postingKind = null; IsPostingDialogOpen = false; Notify(); }
    private void EditorChanged() { Changed(nameof(SupplierName)); Changed(nameof(InvoiceNumber)); Changed(nameof(Amount)); Changed(nameof(InvoiceDate)); Changed(nameof(DueDate)); Changed(nameof(Note)); }
    private void Changed(string n) => PropertyChanged?.Invoke(this, new(n)); private void Notify() { foreach (var n in new[] { nameof(SelectedProject), nameof(SelectedInvoice), nameof(ProjectCurrency), nameof(IsBusy), nameof(IsEditorOpen), nameof(IsArchiveDialogOpen), nameof(IsPostingDialogOpen), nameof(PostingKind), nameof(PostingSupplierName), nameof(PostingInvoiceNumber), nameof(PostingAmount), nameof(PostingCurrency), nameof(PostingInvoiceDate), nameof(LastProjectsReloadSucceeded), nameof(OperationMessage), nameof(InvoiceTotal), nameof(CanNavigate), nameof(CanSelectProject), nameof(CanSelectInvoice), nameof(CanRefresh), nameof(CanAddInvoice), nameof(CanEditInvoice), nameof(CanArchiveInvoice), nameof(CanPostInvoice), nameof(CanConfirmPosting), nameof(CanCancelPosting), nameof(CanSaveInvoice), nameof(CanCancelEditor), nameof(ArchiveSupplierName), nameof(ArchiveInvoiceNumber) }) Changed(n); }
    public void ReportPresentationFailure() { OperationMessage = "Nie udało się wykonać operacji."; Notify(); }
    private sealed record ArchiveTarget(Guid Id, long Version, string Supplier, string Number);
    private sealed record PostingTarget(Guid Id, long Version, string Supplier, string Number, decimal Amount, string Currency, DateOnly InvoiceDate);
}
