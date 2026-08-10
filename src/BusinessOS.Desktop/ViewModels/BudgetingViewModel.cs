using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using BusinessOS.Modules.Budgeting.Application;
using BusinessOS.Modules.Budgeting.Domain;

namespace BusinessOS.Desktop.ViewModels;

public sealed class BudgetingViewModel : INotifyPropertyChanged
{
    private readonly IBudgetingCrudService service;
    private readonly IBudgetingProjectLookup projects;
    private BudgetProjectInfo? selectedProject;
    private BudgetItem? selectedBudget;
    private BudgetVersionItem? selectedVersion;
    private BudgetLineItem? selectedLine;
    private Guid? editingBudgetId;
    private Guid? editingLineId;
    private bool isBusy, isBudgetEditorOpen, isLineEditorOpen, isLifecycleDialogOpen;
    private string operationMessage = string.Empty;

    public BudgetingViewModel(IBudgetingCrudService service, IBudgetingProjectLookup projects)
    { this.service = service; this.projects = projects; LineKinds = Enum.GetValues<BudgetLineKind>(); }

    public ObservableCollection<BudgetProjectInfo> Projects { get; } = [];
    public ObservableCollection<BudgetItem> Budgets { get; } = [];
    public ObservableCollection<BudgetVersionItem> Versions { get; } = [];
    public ObservableCollection<BudgetLineItem> Lines { get; } = [];
    public IReadOnlyList<BudgetLineKind> LineKinds { get; }
    public BudgetProjectInfo? SelectedProject => selectedProject;
    public BudgetItem? SelectedBudget => selectedBudget;
    public BudgetVersionItem? SelectedVersion => selectedVersion;
    public BudgetLineItem? SelectedLine { get => selectedLine; set { if (!CanSelectLine || selectedLine?.Id == value?.Id) return; selectedLine = value; OnPropertyChanged(); NotifyCapabilities(); } }
    public bool IsBusy { get => isBusy; private set { if (Set(ref isBusy, value)) NotifyCapabilities(); } }
    public bool IsBudgetEditorOpen { get => isBudgetEditorOpen; private set { if (Set(ref isBudgetEditorOpen, value)) NotifyCapabilities(); } }
    public bool IsLineEditorOpen { get => isLineEditorOpen; private set { if (Set(ref isLineEditorOpen, value)) NotifyCapabilities(); } }
    public bool IsLifecycleDialogOpen { get => isLifecycleDialogOpen; private set { if (Set(ref isLifecycleDialogOpen, value)) NotifyCapabilities(); } }
    public bool HasOpenInteraction => IsBudgetEditorOpen || IsLineEditorOpen || IsLifecycleDialogOpen;
    public bool LastProjectsReloadSucceeded { get; private set; }
    public bool IsEmpty => SelectedProject is not null && Budgets.Count == 0;
    public string ProjectCurrency => SelectedProject?.BaseCurrency ?? string.Empty;
    public decimal CapexTotal => Total(BudgetLineKind.Capex);
    public decimal OpexTotal => Total(BudgetLineKind.Opex);
    public decimal RevenueTotal => Total(BudgetLineKind.Revenue);
    public decimal FinancingTotal => Total(BudgetLineKind.Financing);
    public string OperationMessage { get => operationMessage; private set => Set(ref operationMessage, value); }
    public string BudgetName { get; set; } = string.Empty;
    public BudgetLineKind LineKind { get; set; } = BudgetLineKind.Capex;
    public string LineName { get; set; } = string.Empty;
    public string LineAmount { get; set; } = "0";
    public string LineSortOrder { get; set; } = "0";
    public string LineNote { get; set; } = string.Empty;

    private bool Draft => SelectedBudget?.Status == BudgetStatus.Draft;
    private bool LatestSelected => SelectedVersion is not null && SelectedVersion.Number == SelectedBudget?.LatestVersion;
    public bool CanNavigate => !IsBusy && !HasOpenInteraction;
    public bool CanSelectProject => CanNavigate;
    public bool CanRefresh => CanNavigate;
    public bool CanAddBudget => CanNavigate && SelectedProject is not null;
    public bool CanRenameBudget => CanNavigate && Draft;
    private BudgetVersionItem? LatestVersion => SelectedBudget is null ? null : Versions.FirstOrDefault(x => x.Number == SelectedBudget.LatestVersion);
    public bool CanActivateBudget => CanNavigate && Draft && LatestVersion is { Lines.Count: > 0 };
    public bool CanArchiveBudget => CanNavigate && SelectedBudget is not null && SelectedBudget.Status != BudgetStatus.Archived;
    public bool CanCreateInitialVersion => CanNavigate && Draft && SelectedBudget!.LatestVersion == 0;
    public bool CanCreateNextVersion => CanNavigate && Draft && SelectedBudget!.LatestVersion > 0;
    public bool CanSelectBudget => !IsBusy && !HasOpenInteraction;
    public bool CanSelectVersion => CanSelectBudget && SelectedBudget is not null;
    public bool CanSelectLine => CanSelectVersion && SelectedVersion is not null;
    public bool CanAddLine => CanNavigate && Draft && LatestSelected;
    public bool CanEditLine => CanAddLine && SelectedLine is not null;
    public bool CanRemoveLine => CanEditLine;
    public bool CanSaveBudget => IsBudgetEditorOpen && !IsBusy;
    public bool CanCancelBudgetEditor => IsBudgetEditorOpen && !IsBusy;
    public bool CanSaveLine => IsLineEditorOpen && !IsBusy;
    public bool CanCancelLineEditor => IsLineEditorOpen && !IsBusy;

    public Task InitializeAsync(CancellationToken ct = default) => ReloadProjectsAsync(ct);
    public async Task ReloadProjectsAsync(CancellationToken ct = default)
    {
        if (!CanNavigate) { LastProjectsReloadSucceeded = false; return; }
        IsBusy = true; LastProjectsReloadSucceeded = false;
        try
        {
            var projectId = selectedProject?.Id; var budgetId = selectedBudget?.Id; var versionId = selectedVersion?.Id;
            Replace(Projects, await projects.ListAvailableAsync(ct));
            selectedProject = projectId is null ? null : Projects.FirstOrDefault(x => x.Id == projectId);
            OnPropertyChanged(nameof(SelectedProject)); OnPropertyChanged(nameof(ProjectCurrency));
            if (selectedProject is null) ClearBudgets(); else await ReloadBudgets(ct, budgetId, versionId);
            LastProjectsReloadSucceeded = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { OperationMessage = "Odświeżanie projektów zostało anulowane."; }
        catch { OperationMessage = "Nie udało się załadować dostępnych projektów."; }
        finally { IsBusy = false; OnPropertyChanged(nameof(LastProjectsReloadSucceeded)); }
    }
    public async Task SelectProjectAsync(BudgetProjectInfo? project, CancellationToken ct = default)
    {
        if (!CanSelectProject || selectedProject?.Id == project?.Id) return;
        IsBusy = true;
        try { var loaded = project is null ? [] : await service.ListBudgetsAsync(project.Id, ct); selectedProject = project; OnPropertyChanged(nameof(SelectedProject)); OnPropertyChanged(nameof(ProjectCurrency)); ReplaceBudgets(loaded); OperationMessage = string.Empty; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { OperationMessage = "Wybór projektu został anulowany."; }
        catch (BudgetingReadException) { OnPropertyChanged(nameof(SelectedProject)); OperationMessage = "Nie udało się załadować budżetów projektu."; }
        catch { OnPropertyChanged(nameof(SelectedProject)); OperationMessage = "Nie udało się załadować budżetów projektu."; }
        finally { IsBusy = false; }
    }
    public async Task RefreshAsync(CancellationToken ct = default) { if (!CanRefresh) return; var budgetId = selectedBudget?.Id; var versionId = selectedVersion?.Id; await Mutating(async () => { await ReloadBudgets(ct, budgetId, versionId); OperationMessage = "Budżety zostały odświeżone."; }, "Nie udało się odświeżyć budżetów."); }
    public void BeginCreateBudget() { if (!CanAddBudget) return; editingBudgetId = null; BudgetName = string.Empty; IsBudgetEditorOpen = true; OnPropertyChanged(nameof(BudgetName)); }
    public void BeginRenameBudget() { if (!CanRenameBudget) return; editingBudgetId = SelectedBudget!.Id; BudgetName = SelectedBudget.Name; IsBudgetEditorOpen = true; OnPropertyChanged(nameof(BudgetName)); }
    public void CancelBudgetEditor() { if (!IsBusy) { editingBudgetId = null; IsBudgetEditorOpen = false; } }
    public async Task SaveBudgetAsync(CancellationToken ct = default)
    {
        if (!CanSaveBudget) return; IsBusy = true;
        try { var r = editingBudgetId is null ? await service.CreateBudgetAsync(SelectedProject!.Id, BudgetName, ct) : await service.RenameBudgetAsync(editingBudgetId.Value, SelectedBudget!.Version, BudgetName, ct); OperationMessage = r.SafeMessage; if (r.Status == BudgetingOperationStatus.Success) { IsBudgetEditorOpen = false; editingBudgetId = null; await ReloadBudgets(ct, r.Value?.Id); OperationMessage = r.SafeMessage; } else if (r.Status == BudgetingOperationStatus.ConcurrencyConflict) await ReloadBudgets(ct, editingBudgetId); }
        catch { OperationMessage = "Nie udało się zapisać budżetu."; }
        finally { IsBusy = false; }
    }
    public void OpenActivateDialog() { if (CanActivateBudget) IsLifecycleDialogOpen = true; }
    public void OpenArchiveDialog() { if (CanArchiveBudget) IsLifecycleDialogOpen = true; }
    public void CloseLifecycleDialog() { if (!IsBusy) IsLifecycleDialogOpen = false; }
    public Task ActivateSelectedBudgetAsync(CancellationToken ct = default) => Lifecycle(async () => await service.ActivateBudgetAsync(SelectedBudget!.Id, SelectedBudget.Version, ct), ct);
    public Task ArchiveSelectedBudgetAsync(CancellationToken ct = default) => Lifecycle(async () => { var r = await service.ArchiveBudgetAsync(SelectedBudget!.Id, SelectedBudget.Version, ct); return new BudgetingResult<BudgetItem>(r.Status, r.SafeMessage, null); }, ct);
    private async Task Lifecycle(Func<Task<BudgetingResult<BudgetItem>>> action, CancellationToken ct) { if (!IsLifecycleDialogOpen || IsBusy) return; IsBusy = true; try { var id = SelectedBudget?.Id; var r = await action(); OperationMessage = r.SafeMessage; IsLifecycleDialogOpen = false; await ReloadBudgets(ct, id); OperationMessage = r.SafeMessage; } catch { OperationMessage = "Nie udało się zmienić stanu budżetu."; } finally { IsLifecycleDialogOpen = false; IsBusy = false; } }
    public Task CreateInitialVersionAsync(CancellationToken ct = default) => CreateVersion(true, ct);
    public Task CreateNextVersionAsync(CancellationToken ct = default) => CreateVersion(false, ct);
    private async Task CreateVersion(bool initial, CancellationToken ct) { if (initial ? !CanCreateInitialVersion : !CanCreateNextVersion) return; await Mutating(async () => { var r = initial ? await service.CreateInitialVersionAsync(SelectedBudget!.Id, SelectedBudget.Version, null, ct) : await service.CreateNextVersionAsync(SelectedBudget!.Id, SelectedBudget.Version, null, ct); OperationMessage = r.SafeMessage; if (r.Status == BudgetingOperationStatus.Success) await ReloadBudgets(ct, SelectedBudget.Id, r.Value?.Id); else if (r.Status == BudgetingOperationStatus.ConcurrencyConflict) await ReloadBudgets(ct, SelectedBudget.Id); }, "Nie udało się utworzyć wersji."); }
    public Task SelectVersionAsync(BudgetVersionItem? version, CancellationToken ct = default)
    {
        if (!CanSelectVersion) return Task.CompletedTask;
        BudgetVersionItem? currentVersion = null;
        if (version is not null)
        {
            currentVersion = Versions.FirstOrDefault(x => x.Id == version.Id);
            if (currentVersion is null) { OnPropertyChanged(nameof(SelectedVersion)); return Task.CompletedTask; }
        }
        selectedVersion = currentVersion; selectedLine = null; Replace(Lines, currentVersion?.Lines ?? []); NotifySelection(); return Task.CompletedTask;
    }
    public void BeginAddLine() { if (!CanAddLine) return; editingLineId = null; LineKind = BudgetLineKind.Capex; LineName = LineNote = string.Empty; LineAmount = LineSortOrder = "0"; IsLineEditorOpen = true; NotifyLineEditor(); }
    public void BeginEditLine() { if (!CanEditLine) return; var l = SelectedLine!; editingLineId = l.Id; LineKind = l.Kind; LineName = l.Name; LineAmount = l.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture); LineSortOrder = l.SortOrder.ToString(System.Globalization.CultureInfo.InvariantCulture); LineNote = l.Note ?? string.Empty; IsLineEditorOpen = true; NotifyLineEditor(); }
    public void CancelLineEditor() { if (!IsBusy) { editingLineId = null; IsLineEditorOpen = false; } }
    public async Task SaveLineAsync(CancellationToken ct = default)
    {
        if (!CanSaveLine) return;
        if (!decimal.TryParse(LineAmount, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var amount) || amount < 0 || !int.TryParse(LineSortOrder, out var order)) { OperationMessage = "Popraw kwotę i kolejność linii."; return; }
        await Mutating(async () => { var r = editingLineId is null ? await service.AddLineAsync(SelectedVersion!.Id, LineKind, LineName, amount, ProjectCurrency, order, LineNote, ct) : await service.UpdateLineAsync(editingLineId.Value, LineKind, LineName, amount, ProjectCurrency, order, LineNote, ct); OperationMessage = r.SafeMessage; if (r.Status == BudgetingOperationStatus.Success) { IsLineEditorOpen = false; editingLineId = null; await ReloadBudgets(ct, SelectedBudget!.Id, SelectedVersion!.Id); OperationMessage = r.SafeMessage; } }, "Nie udało się zapisać linii.");
    }
    public async Task RemoveSelectedLineAsync(CancellationToken ct = default) { if (!CanRemoveLine) return; await Mutating(async () => { var r = await service.RemoveLineAsync(SelectedLine!.Id, ct); OperationMessage = r.SafeMessage; if (r.Status == BudgetingOperationStatus.Success) await ReloadBudgets(ct, SelectedBudget!.Id, SelectedVersion!.Id); }, "Nie udało się usunąć linii."); }
    public void ReportPresentationFailure() => OperationMessage = "Nie udało się wykonać operacji. Spróbuj ponownie.";

    private async Task Mutating(Func<Task> work, string failure) { IsBusy = true; try { await work(); } catch (OperationCanceledException) { OperationMessage = "Operacja została anulowana."; } catch { OperationMessage = failure; } finally { IsBusy = false; } }
    private async Task ReloadBudgets(CancellationToken ct, Guid? budgetId = null, Guid? versionId = null)
    {
        if (SelectedProject is null) { ClearBudgets(); return; }
        var candidateBudgets = await service.ListBudgetsAsync(SelectedProject.Id, ct);
        var candidateBudget = budgetId is null ? null : candidateBudgets.FirstOrDefault(x => x.Id == budgetId);
        IReadOnlyList<BudgetVersionItem> candidateVersions = [];
        BudgetVersionItem? candidateVersion = null;
        if (candidateBudget is not null)
        {
            candidateVersions = await service.ListVersionsAsync(candidateBudget.Id, ct);
            candidateVersion = versionId is null
                ? candidateVersions.OrderByDescending(x => x.Number).FirstOrDefault()
                : candidateVersions.FirstOrDefault(x => x.Id == versionId);
        }
        Replace(Budgets, candidateBudgets); selectedBudget = candidateBudget;
        Replace(Versions, candidateVersions); selectedVersion = candidateVersion; selectedLine = null;
        Replace(Lines, candidateVersion?.Lines ?? []); NotifySelection();
    }
    private void ReplaceBudgets(IReadOnlyList<BudgetItem> items, Guid? budgetId = null, Guid? versionId = null) { Replace(Budgets, items); selectedBudget = budgetId is null ? null : Budgets.FirstOrDefault(x => x.Id == budgetId); Replace(Versions, []); Replace(Lines, []); selectedVersion = null; selectedLine = null; NotifySelection(); }
    public async Task SelectBudgetAsync(BudgetItem? budget, CancellationToken ct = default)
    {
        if (!CanSelectBudget || selectedBudget?.Id == budget?.Id) return;
        IsBusy = true;
        try
        {
            IReadOnlyList<BudgetVersionItem> candidateVersions = budget is null ? [] : await service.ListVersionsAsync(budget.Id, ct);
            var candidateVersion = candidateVersions.OrderByDescending(x => x.Number).FirstOrDefault();
            selectedBudget = budget; Replace(Versions, candidateVersions); selectedVersion = candidateVersion; selectedLine = null;
            Replace(Lines, candidateVersion?.Lines ?? []); NotifySelection();
        }
        catch { OnPropertyChanged(nameof(SelectedBudget)); OperationMessage = "Nie udało się załadować wersji budżetu."; }
        finally { IsBusy = false; }
    }
    private void ClearBudgets() => ReplaceBudgets([]);
    private decimal Total(BudgetLineKind kind) => Lines.Where(x => x.Kind == kind).Sum(x => x.Amount);
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source) { target.Clear(); foreach (var item in source) target.Add(item); }
    private void NotifySelection() { foreach (var n in new[] { nameof(SelectedBudget), nameof(SelectedVersion), nameof(SelectedLine), nameof(IsEmpty), nameof(CapexTotal), nameof(OpexTotal), nameof(RevenueTotal), nameof(FinancingTotal) }) OnPropertyChanged(n); NotifyCapabilities(); }
    private void NotifyLineEditor() { foreach (var n in new[] { nameof(LineKind), nameof(LineName), nameof(LineAmount), nameof(LineSortOrder), nameof(LineNote) }) OnPropertyChanged(n); }
    private void NotifyCapabilities() { foreach (var n in new[] { nameof(CanNavigate), nameof(CanSelectProject), nameof(CanRefresh), nameof(CanAddBudget), nameof(CanRenameBudget), nameof(CanActivateBudget), nameof(CanArchiveBudget), nameof(CanCreateInitialVersion), nameof(CanCreateNextVersion), nameof(CanSelectBudget), nameof(CanSelectVersion), nameof(CanSelectLine), nameof(CanAddLine), nameof(CanEditLine), nameof(CanRemoveLine), nameof(CanSaveBudget), nameof(CanCancelBudgetEditor), nameof(CanSaveLine), nameof(CanCancelLineEditor), nameof(HasOpenInteraction), nameof(IsEmpty) }) OnPropertyChanged(n); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? n = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; OnPropertyChanged(n); return true; }
}
