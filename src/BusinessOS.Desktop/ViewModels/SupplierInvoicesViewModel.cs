using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using BusinessOS.Modules.Budgeting.Application;

namespace BusinessOS.Desktop.ViewModels;

public sealed class SupplierInvoicesViewModel(ISupplierInvoicesCrudService service, IBudgetingProjectLookup projects, TimeProvider clock) : INotifyPropertyChanged
{
    private BudgetProjectInfo? selectedProject; private SupplierInvoiceItem? selectedInvoice; private bool busy; private Guid? editingId; private long editingVersion; private ArchiveTarget? archiveTarget;
    public ObservableCollection<BudgetProjectInfo> Projects { get; } = []; public ObservableCollection<SupplierInvoiceItem> Invoices { get; } = [];
    public BudgetProjectInfo? SelectedProject => selectedProject; public SupplierInvoiceItem? SelectedInvoice => selectedInvoice; public string ProjectCurrency => selectedProject?.BaseCurrency ?? string.Empty;
    public string SupplierName { get; set; } = string.Empty; public string InvoiceNumber { get; set; } = string.Empty; public string Amount { get; set; } = string.Empty; public DateOnly InvoiceDate { get; set; }
    public DateOnly DueDate { get; set; }
    public string Note { get; set; } = string.Empty;
    public bool IsBusy { get => busy; private set { busy = value; Notify(); } }
    public bool IsEditorOpen { get; private set; }
    public bool IsArchiveDialogOpen { get; private set; }
    public bool LastProjectsReloadSucceeded { get; private set; }
    public string OperationMessage { get; private set; } = string.Empty;
    public decimal InvoiceTotal => Invoices.Sum(x => x.Amount); public bool CanNavigate => !IsBusy && !IsEditorOpen && !IsArchiveDialogOpen; public bool CanSelectProject => CanNavigate; public bool CanSelectInvoice => CanNavigate && selectedProject is not null; public bool CanRefresh => CanNavigate && selectedProject is not null; public bool CanAddInvoice => CanRefresh; public bool CanEditInvoice => CanRefresh && selectedInvoice is not null; public bool CanArchiveInvoice => CanEditInvoice; public bool CanSaveInvoice => IsEditorOpen && !IsBusy; public bool CanCancelEditor => IsEditorOpen && !IsBusy;
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
    private async Task Run(Func<Task> action, CancellationToken ct, bool rollbackSelection = false, Action? finallyAction = null) { IsBusy = true; try { await action(); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { OperationMessage = "Operacja została anulowana."; } catch (Exception e) when (e is SupplierInvoicesReadException or BudgetingProjectLookupException) { OperationMessage = "Nie udało się odczytać faktur."; if (rollbackSelection) Changed(nameof(SelectedProject)); } finally { finallyAction?.Invoke(); IsBusy = false; Notify(); } }
    private void Commit(BudgetProjectInfo? project, IReadOnlyList<SupplierInvoiceItem> rows, Guid? select = null) { selectedProject = project; Invoices.Clear(); foreach (var x in rows) Invoices.Add(x); selectedInvoice = select.HasValue ? Invoices.SingleOrDefault(x => x.Id == select) : null; Notify(); }
    private void ClearArchive() { archiveTarget = null; IsArchiveDialogOpen = false; Notify(); }
    private void EditorChanged() { Changed(nameof(SupplierName)); Changed(nameof(InvoiceNumber)); Changed(nameof(Amount)); Changed(nameof(InvoiceDate)); Changed(nameof(DueDate)); Changed(nameof(Note)); }
    private void Changed(string n) => PropertyChanged?.Invoke(this, new(n)); private void Notify() { foreach (var n in new[] { nameof(SelectedProject), nameof(SelectedInvoice), nameof(ProjectCurrency), nameof(IsBusy), nameof(IsEditorOpen), nameof(IsArchiveDialogOpen), nameof(LastProjectsReloadSucceeded), nameof(OperationMessage), nameof(InvoiceTotal), nameof(CanNavigate), nameof(CanSelectProject), nameof(CanSelectInvoice), nameof(CanRefresh), nameof(CanAddInvoice), nameof(CanEditInvoice), nameof(CanArchiveInvoice), nameof(CanSaveInvoice), nameof(CanCancelEditor), nameof(ArchiveSupplierName), nameof(ArchiveInvoiceNumber) }) Changed(n); }
    public void ReportPresentationFailure() { OperationMessage = "Nie udało się wykonać operacji."; Notify(); }
    private sealed record ArchiveTarget(Guid Id, long Version, string Supplier, string Number);
}
