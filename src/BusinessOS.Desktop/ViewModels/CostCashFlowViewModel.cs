using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using BusinessOS.Modules.Budgeting.Application;

namespace BusinessOS.Desktop.ViewModels;

public sealed record CostCashFlowMonthItem(CostCashFlowMonth Value)
{
    private static string F(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    public DateOnly Month => Value.Month;
    public string MonthLabel => Month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
    public string CapexActual => F(Value.Capex.Actual); public string CapexForecast => F(Value.Capex.Forecast); public string CapexExpected => F(Value.Capex.Expected);
    public string OpexActual => F(Value.Opex.Actual); public string OpexForecast => F(Value.Opex.Forecast); public string OpexExpected => F(Value.Opex.Expected);
    public string TotalActual => F(Value.Total.Actual); public string TotalForecast => F(Value.Total.Forecast); public string TotalExpected => F(Value.Total.Expected);
    public string SemanticName => $"{MonthLabel} | CAPEX A={CapexActual} F={CapexForecast} E={CapexExpected} | OPEX A={OpexActual} F={OpexForecast} E={OpexExpected} | TOTAL A={TotalActual} F={TotalForecast} E={TotalExpected}";
}

public sealed class CostCashFlowViewModel(ICostCashFlowQueryService service, IBudgetingProjectLookup projects) : INotifyPropertyChanged
{
    private BudgetProjectInfo? selectedProject; private CostCashFlowSnapshot? snapshot; private bool isBusy; private string operationMessage = string.Empty;
    public ObservableCollection<BudgetProjectInfo> Projects { get; } = [];
    public ObservableCollection<CostCashFlowMonthItem> Months { get; } = [];
    public BudgetProjectInfo? SelectedProject => selectedProject; public CostCashFlowSnapshot? Snapshot => snapshot;
    public string ProjectCurrency => snapshot?.Currency ?? selectedProject?.BaseCurrency ?? string.Empty;
    public string OperationMessage { get => operationMessage; private set => Set(ref operationMessage, value); }
    public bool IsBusy { get => isBusy; private set { if (Set(ref isBusy, value)) NotifyCapabilities(); } }
    public bool LastProjectsReloadSucceeded { get; private set; }
    public bool CanNavigate => !IsBusy; public bool CanSelectProject => !IsBusy; public bool CanRefresh => !IsBusy && selectedProject is not null;
    public string CapexActualTotal => F(snapshot?.Capex.Actual); public string CapexForecastTotal => F(snapshot?.Capex.Forecast); public string CapexExpectedTotal => F(snapshot?.Capex.Expected);
    public string OpexActualTotal => F(snapshot?.Opex.Actual); public string OpexForecastTotal => F(snapshot?.Opex.Forecast); public string OpexExpectedTotal => F(snapshot?.Opex.Expected);
    public string ActualTotal => F(snapshot?.Total.Actual); public string ForecastTotal => F(snapshot?.Total.Forecast); public string ExpectedTotal => F(snapshot?.Total.Expected);
    public bool HasEmptySnapshot => snapshot is not null && Months.Count == 0;

    public async Task ReloadProjectsAsync(CancellationToken ct = default)
    {
        if (!CanNavigate) { LastProjectsReloadSucceeded = false; return; }
        LastProjectsReloadSucceeded = false; IsBusy = true;
        try
        {
            var candidateProjects = await projects.ListAvailableAsync(ct);
            var candidateProject = selectedProject is null ? null : candidateProjects.SingleOrDefault(x => x.Id == selectedProject.Id);
            var candidateSnapshot = candidateProject is null ? null : await service.GetSnapshotAsync(candidateProject.Id, ct);
            if (candidateProject is not null && candidateSnapshot is null) throw new CostCashFlowReadException(new InvalidDataException());
            Replace(Projects, candidateProjects); Commit(candidateProject, candidateSnapshot); OperationMessage = string.Empty; LastProjectsReloadSucceeded = true;
        }
        catch (Exception e) when (e is CostCashFlowReadException or BudgetingProjectLookupException) { OperationMessage = "Nie udało się wczytać cash flow kosztów."; NotifyState(); }
        finally { IsBusy = false; }
    }
    public async Task SelectProjectAsync(BudgetProjectInfo project, CancellationToken ct = default)
    {
        var canonical = Projects.SingleOrDefault(x => x.Id == project.Id); if (!CanSelectProject || canonical is null) return;
        await ReadCandidateAsync(canonical, ct);
    }
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (!CanRefresh || selectedProject is null) return; await ReadCandidateAsync(selectedProject, ct);
    }
    private async Task ReadCandidateAsync(BudgetProjectInfo project, CancellationToken ct)
    {
        IsBusy = true;
        try { var value = await service.GetSnapshotAsync(project.Id, ct); if (value is null) { OperationMessage = "Nie znaleziono cash flow kosztów."; NotifyState(); return; } Commit(project, value); OperationMessage = string.Empty; }
        catch (Exception e) when (e is CostCashFlowReadException or BudgetingProjectLookupException) { OperationMessage = "Nie udało się wczytać cash flow kosztów."; NotifyState(); }
        finally { IsBusy = false; }
    }
    public void ReportPresentationFailure() => OperationMessage = "Nie udało się wyświetlić cash flow kosztów.";
    private void Commit(BudgetProjectInfo? project, CostCashFlowSnapshot? value) { selectedProject = project; snapshot = value; Replace(Months, value?.Months.Select(x => new CostCashFlowMonthItem(x)).ToArray() ?? []); NotifyState(); }
    private void NotifyState() { foreach (var name in new[] { nameof(SelectedProject), nameof(Snapshot), nameof(ProjectCurrency), nameof(Months), nameof(HasEmptySnapshot), nameof(CapexActualTotal), nameof(CapexForecastTotal), nameof(CapexExpectedTotal), nameof(OpexActualTotal), nameof(OpexForecastTotal), nameof(OpexExpectedTotal), nameof(ActualTotal), nameof(ForecastTotal), nameof(ExpectedTotal) }) Changed(name); NotifyCapabilities(); }
    private void NotifyCapabilities() { Changed(nameof(CanNavigate)); Changed(nameof(CanSelectProject)); Changed(nameof(CanRefresh)); }
    private static string F(decimal? value) => value?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty;
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values) { target.Clear(); foreach (var value in values) target.Add(value); }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Changed(name); return true; }
    private void Changed(string? name) => PropertyChanged?.Invoke(this, new(name)); public event PropertyChangedEventHandler? PropertyChanged;
}
