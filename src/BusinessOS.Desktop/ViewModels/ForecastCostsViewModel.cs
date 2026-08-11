using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using BusinessOS.Modules.Budgeting.Application;
using BusinessOS.Modules.Budgeting.Domain;

namespace BusinessOS.Desktop.ViewModels;

public sealed class ForecastCostsViewModel(IForecastCostsCrudService service, IBudgetingProjectLookup projects, TimeProvider clock) : INotifyPropertyChanged
{
    private BudgetProjectInfo? selectedProject;
    private ForecastCostItem? selectedForecastCost;
    private bool isBusy, isEditorOpen, isArchiveDialogOpen;
    private Guid? editingId;
    private ArchiveTarget? archiveTarget;
    private string operationMessage = string.Empty;
    public ObservableCollection<BudgetProjectInfo> Projects { get; } = [];
    public ObservableCollection<ForecastCostItem> ForecastCosts { get; } = [];
    public IReadOnlyList<ForecastCostKind> ForecastKinds { get; } = Enum.GetValues<ForecastCostKind>();
    public BudgetProjectInfo? SelectedProject => selectedProject;
    public ForecastCostItem? SelectedForecastCost => selectedForecastCost;
    public string ProjectCurrency => selectedProject?.BaseCurrency ?? string.Empty;
    public decimal ForecastCapexTotal => ForecastCosts.Where(x => x.Kind == ForecastCostKind.Capex).Sum(x => x.Amount);
    public decimal ForecastOpexTotal => ForecastCosts.Where(x => x.Kind == ForecastCostKind.Opex).Sum(x => x.Amount);
    public decimal ForecastTotal => ForecastCapexTotal + ForecastOpexTotal;
    public bool IsBusy { get => isBusy; private set { if (Set(ref isBusy, value)) Notify(); } }
    public bool IsEditorOpen { get => isEditorOpen; private set { if (Set(ref isEditorOpen, value)) Notify(); } }
    public bool IsArchiveDialogOpen { get => isArchiveDialogOpen; private set { if (Set(ref isArchiveDialogOpen, value)) Notify(); } }
    public bool HasOpenInteraction => IsEditorOpen || IsArchiveDialogOpen;
    public bool LastProjectsReloadSucceeded { get; private set; }
    public string OperationMessage { get => operationMessage; private set => Set(ref operationMessage, value); }
    public string? ArchivingForecastName => archiveTarget?.Name;
    public ForecastCostKind ForecastKind { get; set; }
    public string ForecastName { get; set; } = string.Empty;
    public string ForecastAmount { get; set; } = string.Empty;
    public DateOnly ForecastExpectedOn { get; set; }
    public string ForecastNote { get; set; } = string.Empty;
    public bool CanNavigate => !IsBusy && !HasOpenInteraction;
    public bool CanSelectProject => CanNavigate;
    public bool CanSelectForecast => CanNavigate && SelectedProject is not null;
    public bool CanRefresh => CanNavigate && SelectedProject is not null;
    public bool CanAddForecast => CanRefresh;
    public bool CanEditForecast => CanRefresh && SelectedForecastCost is not null;
    public bool CanArchiveForecast => CanEditForecast;
    public bool CanSaveForecast => IsEditorOpen && !IsBusy;
    public bool CanCancelEditor => CanSaveForecast;

    public async Task ReloadProjectsAsync(CancellationToken ct = default)
    {
        if (!CanNavigate) { LastProjectsReloadSucceeded = false; return; }
        LastProjectsReloadSucceeded = false;
        await Busy(async () =>
        {
            var old = selectedProject?.Id;
            var values = await projects.ListAvailableAsync(ct);
            var candidateProject = old is null ? null : values.SingleOrDefault(x => x.Id == old);
            var candidateForecastCosts = candidateProject is null ? [] : await LoadForecastCostsAsync(candidateProject.Id, ct);
            Projects.Clear(); foreach (var item in values) Projects.Add(item);
            selectedProject = candidateProject;
            ReplaceForecastCosts(candidateForecastCosts);
            OperationMessage = string.Empty;
            LastProjectsReloadSucceeded = true;
            Changed(nameof(SelectedProject)); Changed(nameof(ProjectCurrency)); Totals();
        });
    }
    public async Task SelectProjectAsync(BudgetProjectInfo project, CancellationToken ct = default)
    {
        if (!CanSelectProject) return;
        var canonical = Projects.SingleOrDefault(x => x.Id == project.Id); if (canonical is null) return;
        var succeeded = await Busy(async () =>
        {
            var candidateForecastCosts = await LoadForecastCostsAsync(canonical.Id, ct);
            selectedProject = canonical;
            ReplaceForecastCosts(candidateForecastCosts);
            OperationMessage = string.Empty;
            Changed(nameof(SelectedProject)); Changed(nameof(ProjectCurrency));
        });
        if (!succeeded) RepublishSnapshot();
    }
    public Task SelectForecastAsync(ForecastCostItem forecast)
    {
        if (CanSelectForecast) { selectedForecastCost = ForecastCosts.SingleOrDefault(x => x.Id == forecast.Id); Changed(nameof(SelectedForecastCost)); Notify(); }
        return Task.CompletedTask;
    }
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (selectedProject is null) return;
        await Busy(async () =>
        {
            var candidate = await LoadForecastCostsAsync(selectedProject.Id, ct);
            ReplaceForecastCosts(candidate);
            OperationMessage = string.Empty;
        });
    }
    public void BeginAddForecast() { if (!CanAddForecast) return; editingId = null; ForecastKind = ForecastCostKind.Capex; ForecastName = ForecastAmount = ForecastNote = string.Empty; ForecastExpectedOn = DateOnly.FromDateTime(clock.GetLocalNow().DateTime).AddDays(30); NotifyEditor(); IsEditorOpen = true; }
    public void BeginEditForecast() { if (!CanEditForecast) return; editingId = selectedForecastCost!.Id; ForecastKind = selectedForecastCost.Kind; ForecastName = selectedForecastCost.Name; ForecastAmount = selectedForecastCost.Amount.ToString(CultureInfo.InvariantCulture); ForecastExpectedOn = selectedForecastCost.ExpectedOn; ForecastNote = selectedForecastCost.Note ?? string.Empty; NotifyEditor(); IsEditorOpen = true; }
    public void CancelEditor() { if (!CanCancelEditor) return; IsEditorOpen = false; }
    public async Task SaveForecastAsync(CancellationToken ct = default)
    {
        if (!CanSaveForecast || selectedProject is null || !decimal.TryParse(ForecastAmount, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)) { OperationMessage = "Popraw wskazane dane."; return; }
        await Busy(async () =>
        {
            var result = editingId is null
                ? await service.CreateAsync(selectedProject.Id, ForecastKind, ForecastName, amount, selectedProject.BaseCurrency, ForecastExpectedOn, ForecastNote, ct)
                : await service.UpdateAsync(editingId.Value, ForecastCosts.Single(x => x.Id == editingId).Version, ForecastKind, ForecastName, amount, selectedProject.BaseCurrency, ForecastExpectedOn, ForecastNote, ct);
            OperationMessage = result.SafeMessage;
            if (result.Status == ForecastCostOperationStatus.Success) { IsEditorOpen = false; ReplaceForecastCosts(await LoadForecastCostsAsync(selectedProject.Id, ct)); if (result.Value is not null) selectedForecastCost = ForecastCosts.Single(x => x.Id == result.Value.Id); Changed(nameof(SelectedForecastCost)); }
        });
    }
    public void OpenArchiveDialog()
    {
        if (!CanArchiveForecast) return;
        archiveTarget = new(selectedForecastCost!.Id, selectedForecastCost.Version, selectedForecastCost.Name);
        Changed(nameof(ArchivingForecastName));
        IsArchiveDialogOpen = true;
    }
    public void CancelArchive()
    {
        if (!IsArchiveDialogOpen || IsBusy) return;
        ClearArchiveTarget();
        IsArchiveDialogOpen = false;
    }
    public async Task ConfirmArchiveAsync(CancellationToken ct = default)
    {
        if (!IsArchiveDialogOpen || archiveTarget is null) return;
        var target = archiveTarget;
        await Busy(async () =>
        {
            var result = await service.ArchiveAsync(target.Id, target.Version, ct);
            OperationMessage = result.SafeMessage;
            if (result.Status == ForecastCostOperationStatus.Success)
            {
                var candidate = await LoadForecastCostsAsync(selectedProject!.Id, ct);
                ReplaceForecastCosts(candidate);
            }
        });
        ClearArchiveTarget();
        IsArchiveDialogOpen = false;
    }
    public void ReportPresentationFailure() => OperationMessage = "Nie udało się wyświetlić danych kosztów.";
    private Task<IReadOnlyList<ForecastCostItem>> LoadForecastCostsAsync(Guid projectId, CancellationToken ct) => service.ListAsync(projectId, ct);
    private void ReplaceForecastCosts(IReadOnlyList<ForecastCostItem> values) { ForecastCosts.Clear(); foreach (var item in values) ForecastCosts.Add(item); selectedForecastCost = null; Changed(nameof(SelectedForecastCost)); Totals(); }
    private async Task<bool> Busy(Func<Task> work)
    {
        if (IsBusy) return false;
        IsBusy = true;
        try { await work(); return true; }
        catch (Exception exception) when (exception is ForecastCostsReadException or BudgetingProjectLookupException)
        { OperationMessage = "Nie udało się wczytać kosztów."; return false; }
        finally { IsBusy = false; }
    }
    private void RepublishSnapshot() { Changed(nameof(SelectedProject)); Changed(nameof(ProjectCurrency)); Changed(nameof(SelectedForecastCost)); Totals(); }
    private void ClearArchiveTarget() { archiveTarget = null; Changed(nameof(ArchivingForecastName)); }
    private void NotifyEditor() { foreach (var name in new[] { nameof(ForecastKind), nameof(ForecastName), nameof(ForecastAmount), nameof(ForecastExpectedOn), nameof(ForecastNote) }) Changed(name); Notify(); }
    private void Totals() { Changed(nameof(ForecastCapexTotal)); Changed(nameof(ForecastOpexTotal)); Changed(nameof(ForecastTotal)); Notify(); }
    private void Notify() { foreach (var n in new[] { nameof(CanNavigate), nameof(CanSelectProject), nameof(CanSelectForecast), nameof(CanRefresh), nameof(CanAddForecast), nameof(CanEditForecast), nameof(CanArchiveForecast), nameof(CanSaveForecast), nameof(CanCancelEditor), nameof(HasOpenInteraction) }) Changed(n); }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Changed(name); return true; }
    private void Changed(string? name) => PropertyChanged?.Invoke(this, new(name));
    public event PropertyChangedEventHandler? PropertyChanged;
    private sealed record ArchiveTarget(Guid Id, long Version, string Name);
}
