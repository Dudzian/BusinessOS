using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using BusinessOS.Modules.Budgeting.Application;
using BusinessOS.Modules.Budgeting.Domain;

namespace BusinessOS.Desktop.ViewModels;

public sealed class ActualCostsViewModel(IActualCostsCrudService service, IBudgetingProjectLookup projects, TimeProvider clock) : INotifyPropertyChanged
{
    private BudgetProjectInfo? selectedProject;
    private ActualCostItem? selectedCost;
    private bool isBusy, isEditorOpen, isArchiveDialogOpen;
    private Guid? editingId;
    private string operationMessage = string.Empty;
    public ObservableCollection<BudgetProjectInfo> Projects { get; } = [];
    public ObservableCollection<ActualCostItem> Costs { get; } = [];
    public IReadOnlyList<ActualCostKind> CostKinds { get; } = Enum.GetValues<ActualCostKind>();
    public BudgetProjectInfo? SelectedProject => selectedProject;
    public ActualCostItem? SelectedCost => selectedCost;
    public string ProjectCurrency => selectedProject?.BaseCurrency ?? string.Empty;
    public decimal CapexTotal => Costs.Where(x => x.Kind == ActualCostKind.Capex).Sum(x => x.Amount);
    public decimal OpexTotal => Costs.Where(x => x.Kind == ActualCostKind.Opex).Sum(x => x.Amount);
    public decimal TotalCost => CapexTotal + OpexTotal;
    public bool IsBusy { get => isBusy; private set { if (Set(ref isBusy, value)) Notify(); } }
    public bool IsEditorOpen { get => isEditorOpen; private set { if (Set(ref isEditorOpen, value)) Notify(); } }
    public bool IsArchiveDialogOpen { get => isArchiveDialogOpen; private set { if (Set(ref isArchiveDialogOpen, value)) Notify(); } }
    public bool HasOpenInteraction => IsEditorOpen || IsArchiveDialogOpen;
    public bool LastProjectsReloadSucceeded { get; private set; }
    public string OperationMessage { get => operationMessage; private set => Set(ref operationMessage, value); }
    public ActualCostKind CostKind { get; set; }
    public string CostName { get; set; } = string.Empty;
    public string CostAmount { get; set; } = string.Empty;
    public DateOnly CostDate { get; set; }
    public string CostNote { get; set; } = string.Empty;
    public bool CanNavigate => !IsBusy && !HasOpenInteraction;
    public bool CanSelectProject => CanNavigate;
    public bool CanSelectCost => CanNavigate && SelectedProject is not null;
    public bool CanRefresh => CanNavigate && SelectedProject is not null;
    public bool CanAddCost => CanRefresh;
    public bool CanEditCost => CanRefresh && SelectedCost is not null;
    public bool CanArchiveCost => CanEditCost;
    public bool CanSaveCost => IsEditorOpen && !IsBusy;
    public bool CanCancelEditor => CanSaveCost;

    public async Task ReloadProjectsAsync(CancellationToken ct = default)
    {
        if (!CanNavigate) { LastProjectsReloadSucceeded = false; return; }
        LastProjectsReloadSucceeded = false;
        await Busy(async () =>
        {
            var old = selectedProject?.Id;
            var values = await projects.ListAvailableAsync(ct);
            var candidateProject = old is null ? null : values.SingleOrDefault(x => x.Id == old);
            var candidateCosts = candidateProject is null ? [] : await LoadCostsAsync(candidateProject.Id, ct);
            Projects.Clear(); foreach (var item in values) Projects.Add(item);
            selectedProject = candidateProject;
            ReplaceCosts(candidateCosts);
            LastProjectsReloadSucceeded = true;
            Changed(nameof(SelectedProject)); Changed(nameof(ProjectCurrency)); Totals();
        });
    }
    public async Task SelectProjectAsync(BudgetProjectInfo project, CancellationToken ct = default)
    {
        if (!CanSelectProject) return;
        var canonical = Projects.SingleOrDefault(x => x.Id == project.Id); if (canonical is null) return;
        await Busy(async () =>
        {
            var candidateCosts = await LoadCostsAsync(canonical.Id, ct);
            selectedProject = canonical;
            ReplaceCosts(candidateCosts);
            Changed(nameof(SelectedProject)); Changed(nameof(ProjectCurrency));
        });
    }
    public Task SelectCostAsync(ActualCostItem cost)
    {
        if (CanSelectCost) { selectedCost = Costs.SingleOrDefault(x => x.Id == cost.Id); Changed(nameof(SelectedCost)); Notify(); }
        return Task.CompletedTask;
    }
    public Task RefreshAsync(CancellationToken ct = default) => selectedProject is null ? Task.CompletedTask : Busy(async () => ReplaceCosts(await LoadCostsAsync(selectedProject.Id, ct)));
    public void BeginAddCost() { if (!CanAddCost) return; editingId = null; CostKind = ActualCostKind.Capex; CostName = CostAmount = CostNote = string.Empty; CostDate = DateOnly.FromDateTime(clock.GetLocalNow().DateTime); NotifyEditor(); IsEditorOpen = true; }
    public void BeginEditCost() { if (!CanEditCost) return; editingId = selectedCost!.Id; CostKind = selectedCost.Kind; CostName = selectedCost.Name; CostAmount = selectedCost.Amount.ToString(CultureInfo.InvariantCulture); CostDate = selectedCost.IncurredOn; CostNote = selectedCost.Note ?? string.Empty; NotifyEditor(); IsEditorOpen = true; }
    public void CancelEditor() { if (!CanCancelEditor) return; IsEditorOpen = false; }
    public async Task SaveCostAsync(CancellationToken ct = default)
    {
        if (!CanSaveCost || selectedProject is null || !decimal.TryParse(CostAmount, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)) { OperationMessage = "Popraw wskazane dane."; return; }
        await Busy(async () =>
        {
            var result = editingId is null
                ? await service.CreateAsync(selectedProject.Id, CostKind, CostName, amount, selectedProject.BaseCurrency, CostDate, CostNote, ct)
                : await service.UpdateAsync(editingId.Value, Costs.Single(x => x.Id == editingId).Version, CostKind, CostName, amount, selectedProject.BaseCurrency, CostDate, CostNote, ct);
            OperationMessage = result.SafeMessage;
            if (result.Status == ActualCostOperationStatus.Success) { IsEditorOpen = false; ReplaceCosts(await LoadCostsAsync(selectedProject.Id, ct)); if (result.Value is not null) selectedCost = Costs.Single(x => x.Id == result.Value.Id); Changed(nameof(SelectedCost)); }
        });
    }
    public void OpenArchiveDialog() { if (CanArchiveCost) IsArchiveDialogOpen = true; }
    public void CancelArchive() { if (IsArchiveDialogOpen && !IsBusy) IsArchiveDialogOpen = false; }
    public async Task ConfirmArchiveAsync(CancellationToken ct = default)
    {
        if (!IsArchiveDialogOpen || selectedCost is null) return;
        var cost = selectedCost;
        await Busy(async () => { var result = await service.ArchiveAsync(cost.Id, cost.Version, ct); OperationMessage = result.SafeMessage; IsArchiveDialogOpen = false; if (result.Status == ActualCostOperationStatus.Success) ReplaceCosts(await LoadCostsAsync(selectedProject!.Id, ct)); });
    }
    public void ReportPresentationFailure() => OperationMessage = "Nie udało się wyświetlić danych kosztów.";
    private Task<IReadOnlyList<ActualCostItem>> LoadCostsAsync(Guid projectId, CancellationToken ct) => service.ListAsync(projectId, ct);
    private void ReplaceCosts(IReadOnlyList<ActualCostItem> values) { Costs.Clear(); foreach (var item in values) Costs.Add(item); selectedCost = null; Changed(nameof(SelectedCost)); Totals(); }
    private async Task Busy(Func<Task> work) { if (IsBusy) return; IsBusy = true; try { await work(); } catch (Exception exception) when (exception is ActualCostsReadException or BudgetingProjectLookupException) { OperationMessage = "Nie udało się wczytać kosztów."; LastProjectsReloadSucceeded = false; } finally { IsBusy = false; } }
    private void NotifyEditor() { foreach (var name in new[] { nameof(CostKind), nameof(CostName), nameof(CostAmount), nameof(CostDate), nameof(CostNote) }) Changed(name); Notify(); }
    private void Totals() { Changed(nameof(CapexTotal)); Changed(nameof(OpexTotal)); Changed(nameof(TotalCost)); Notify(); }
    private void Notify() { foreach (var n in new[] { nameof(CanNavigate), nameof(CanSelectProject), nameof(CanSelectCost), nameof(CanRefresh), nameof(CanAddCost), nameof(CanEditCost), nameof(CanArchiveCost), nameof(CanSaveCost), nameof(CanCancelEditor), nameof(HasOpenInteraction) }) Changed(n); }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Changed(name); return true; }
    private void Changed(string? name) => PropertyChanged?.Invoke(this, new(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}
