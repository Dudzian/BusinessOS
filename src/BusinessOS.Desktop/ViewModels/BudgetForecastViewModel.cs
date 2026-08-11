using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using BusinessOS.Modules.Budgeting.Application;

namespace BusinessOS.Desktop.ViewModels;

public sealed class BudgetForecastViewModel(IBudgetForecastQueryService service, IBudgetingProjectLookup projects) : INotifyPropertyChanged
{
    private BudgetProjectInfo? selectedProject;
    private BudgetForecastBudgetItem? selectedBudget;
    private BudgetForecastVersionItem? selectedVersion;
    private BudgetForecastSnapshot? snapshot;
    private bool isBusy;
    private string operationMessage = string.Empty;
    public ObservableCollection<BudgetProjectInfo> Projects { get; } = [];
    public ObservableCollection<BudgetForecastBudgetItem> Budgets { get; } = [];
    public ObservableCollection<BudgetForecastVersionItem> Versions { get; } = [];
    public BudgetProjectInfo? SelectedProject => selectedProject;
    public BudgetForecastBudgetItem? SelectedBudget => selectedBudget;
    public BudgetForecastVersionItem? SelectedVersion => selectedVersion;
    public BudgetForecastSnapshot? Snapshot => snapshot;
    public string ProjectCurrency => snapshot?.Currency ?? selectedProject?.BaseCurrency ?? string.Empty;
    public string BudgetStatus => snapshot?.BudgetStatus.ToString() ?? selectedBudget?.Status.ToString() ?? string.Empty;
    public string VersionLabel => selectedVersion?.Label ?? string.Empty;
    public bool IsBusy { get => isBusy; private set { if (Set(ref isBusy, value)) NotifyCapabilities(); } }
    public bool LastProjectsReloadSucceeded { get; private set; }
    public string OperationMessage { get => operationMessage; private set => Set(ref operationMessage, value); }
    public bool CanNavigate => !IsBusy;
    public bool CanSelectProject => CanNavigate;
    public bool CanSelectBudget => CanNavigate && SelectedProject is not null;
    public bool CanSelectVersion => CanNavigate && SelectedBudget is not null;
    public bool CanRefresh => CanNavigate && SelectedVersion is not null;

    public string CapexPlanned => Money(snapshot?.Capex.Planned); public string CapexActual => Money(snapshot?.Capex.Actual); public string CapexEtc => Money(snapshot?.Capex.EstimateToComplete); public string CapexEac => Money(snapshot?.Capex.EstimateAtCompletion); public string CapexVac => Money(snapshot?.Capex.VarianceAtCompletion); public string CapexUtilization => Percent(snapshot?.Capex.EacUtilizationPercent); public string CapexState => State(snapshot?.Capex.State);
    public string OpexPlanned => Money(snapshot?.Opex.Planned); public string OpexActual => Money(snapshot?.Opex.Actual); public string OpexEtc => Money(snapshot?.Opex.EstimateToComplete); public string OpexEac => Money(snapshot?.Opex.EstimateAtCompletion); public string OpexVac => Money(snapshot?.Opex.VarianceAtCompletion); public string OpexUtilization => Percent(snapshot?.Opex.EacUtilizationPercent); public string OpexState => State(snapshot?.Opex.State);
    public string TotalPlanned => Money(snapshot?.Total.Planned); public string TotalActual => Money(snapshot?.Total.Actual); public string TotalEtc => Money(snapshot?.Total.EstimateToComplete); public string TotalEac => Money(snapshot?.Total.EstimateAtCompletion); public string TotalVac => Money(snapshot?.Total.VarianceAtCompletion); public string TotalUtilization => Percent(snapshot?.Total.EacUtilizationPercent); public string TotalState => State(snapshot?.Total.State);

    public async Task ReloadProjectsAsync(CancellationToken ct = default)
    {
        if (!CanNavigate) { LastProjectsReloadSucceeded = false; return; }
        LastProjectsReloadSucceeded = false;
        var succeeded = await RunReadAsync(async () =>
        {
            var values = await projects.ListAvailableAsync(ct);
            var candidate = selectedProject is null ? null : values.SingleOrDefault(x => x.Id == selectedProject.Id);
            var budgets = candidate is null ? [] : await service.ListBudgetsAsync(candidate.Id, ct);
            Projects.Clear(); foreach (var value in values) Projects.Add(value);
            selectedProject = candidate; Replace(Budgets, budgets); selectedBudget = null; selectedVersion = null; snapshot = null;
            OperationMessage = string.Empty; NotifyState();
        });
        LastProjectsReloadSucceeded = succeeded;
        if (!succeeded) NotifyState();
    }

    public Task SelectProjectAsync(BudgetProjectInfo project, CancellationToken ct = default)
    {
        var canonical = Projects.SingleOrDefault(x => x.Id == project.Id);
        if (!CanSelectProject || canonical is null) return Task.CompletedTask;
        return SelectProjectCoreAsync(canonical, ct);
    }

    public Task SelectBudgetAsync(BudgetForecastBudgetItem budget, CancellationToken ct = default)
    {
        var canonical = Budgets.SingleOrDefault(x => x.Id == budget.Id);
        if (!CanSelectBudget || canonical is null) return Task.CompletedTask;
        return SelectBudgetCoreAsync(canonical, ct);
    }

    public Task SelectVersionAsync(BudgetForecastVersionItem version, CancellationToken ct = default)
    {
        var canonical = Versions.SingleOrDefault(x => x.Id == version.Id && x.BudgetId == selectedBudget?.Id);
        if (!CanSelectVersion || canonical is null || selectedProject is null || selectedBudget is null) return Task.CompletedTask;
        return SelectVersionCoreAsync(canonical, ct);
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (!CanRefresh || selectedProject is null || selectedBudget is null || selectedVersion is null) return;
        var succeeded = await RunReadAsync(async () =>
        {
            var value = await service.GetSnapshotAsync(selectedProject.Id, selectedBudget.Id, selectedVersion.Id, ct);
            if (value is null) { OperationMessage = "Nie znaleziono analizy."; return; }
            snapshot = value; OperationMessage = string.Empty; NotifyState();
        });
        if (!succeeded) NotifyState();
    }
    public void ReportPresentationFailure() => OperationMessage = "Nie udało się wyświetlić analizy.";
    private async Task SelectProjectCoreAsync(BudgetProjectInfo canonical, CancellationToken ct)
    {
        var succeeded = await RunReadAsync(async () =>
        {
            var values = await service.ListBudgetsAsync(canonical.Id, ct);
            selectedProject = canonical; Replace(Budgets, values); selectedBudget = null; Replace(Versions, []); selectedVersion = null; snapshot = null;
            OperationMessage = string.Empty; NotifyState();
        });
        if (!succeeded) NotifyState();
    }
    private async Task SelectBudgetCoreAsync(BudgetForecastBudgetItem canonical, CancellationToken ct)
    {
        var succeeded = await RunReadAsync(async () =>
        {
            var values = await service.ListVersionsAsync(canonical.Id, ct);
            selectedBudget = canonical; Replace(Versions, values); selectedVersion = null; snapshot = null;
            OperationMessage = string.Empty; NotifyState();
        });
        if (!succeeded) NotifyState();
    }
    private async Task SelectVersionCoreAsync(BudgetForecastVersionItem canonical, CancellationToken ct)
    {
        var succeeded = await RunReadAsync(async () =>
        {
            var value = await service.GetSnapshotAsync(selectedProject!.Id, selectedBudget!.Id, canonical.Id, ct);
            if (value is null) { OperationMessage = "Nie znaleziono analizy."; return; }
            selectedVersion = canonical; snapshot = value; OperationMessage = string.Empty; NotifyState();
        });
        if (!succeeded || selectedVersion?.Id != canonical.Id) NotifyState();
    }
    private async Task<bool> RunReadAsync(Func<Task> work)
    {
        IsBusy = true;
        try { await work(); return true; }
        catch (Exception e) when (e is BudgetForecastReadException or BudgetingProjectLookupException)
        { OperationMessage = "Nie udało się wczytać analizy planu i prognozy."; return false; }
        finally { IsBusy = false; }
    }
    private static void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> values) { target.Clear(); foreach (var value in values) target.Add(value); }
    private void NotifyState() { foreach (var name in new[] { nameof(SelectedProject), nameof(SelectedBudget), nameof(SelectedVersion), nameof(Snapshot), nameof(ProjectCurrency), nameof(BudgetStatus), nameof(VersionLabel), nameof(CapexPlanned), nameof(CapexActual), nameof(CapexEtc), nameof(CapexEac), nameof(CapexVac), nameof(CapexUtilization), nameof(CapexState), nameof(OpexPlanned), nameof(OpexActual), nameof(OpexEtc), nameof(OpexEac), nameof(OpexVac), nameof(OpexUtilization), nameof(OpexState), nameof(TotalPlanned), nameof(TotalActual), nameof(TotalEtc), nameof(TotalEac), nameof(TotalVac), nameof(TotalUtilization), nameof(TotalState) }) Changed(name); NotifyCapabilities(); }
    private void NotifyCapabilities() { foreach (var name in new[] { nameof(CanNavigate), nameof(CanSelectProject), nameof(CanSelectBudget), nameof(CanSelectVersion), nameof(CanRefresh) }) Changed(name); }
    private static string Money(decimal? value) => value?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty;
    private static string Percent(decimal? value) => value is null ? "—" : value.Value.ToString("0.##", CultureInfo.InvariantCulture) + "%";
    private static string State(BudgetForecastState? value) => value switch { BudgetForecastState.UnderBudget => "Poniżej budżetu", BudgetForecastState.OnBudget => "Zgodnie z budżetem", BudgetForecastState.OverBudget => "Powyżej budżetu", BudgetForecastState.UnplannedSpend => "Wydatek bez planu", _ => string.Empty };
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Changed(name); return true; }
    private void Changed(string? name) => PropertyChanged?.Invoke(this, new(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}
