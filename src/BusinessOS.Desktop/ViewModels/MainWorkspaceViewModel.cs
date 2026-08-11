using System.ComponentModel;
using System.Runtime.CompilerServices;
namespace BusinessOS.Desktop.ViewModels;

public enum WorkspaceSection { Companies, BusinessProjects, Budgeting, ActualCosts, ForecastCosts, BudgetVariance }
public sealed class MainWorkspaceViewModel : INotifyPropertyChanged
{
    private WorkspaceSection selectedSection;
    public MainWorkspaceViewModel(CompaniesViewModel companies, BusinessProjectsViewModel projects, BudgetingViewModel budgeting, ActualCostsViewModel actualCosts, ForecastCostsViewModel forecastCosts, BudgetVarianceViewModel budgetVariance)
    {
        Companies = companies; Projects = projects; Budgeting = budgeting; ActualCosts = actualCosts; BudgetVariance = budgetVariance; ForecastCosts = forecastCosts;
        Companies.PropertyChanged += ChildChanged; Projects.PropertyChanged += ChildChanged; Budgeting.PropertyChanged += ChildChanged; ActualCosts.PropertyChanged += ChildChanged; BudgetVariance.PropertyChanged += ChildChanged; ForecastCosts.PropertyChanged += ChildChanged;
    }
    public CompaniesViewModel Companies { get; }
    public BusinessProjectsViewModel Projects { get; }
    public BudgetingViewModel Budgeting { get; }
    public ActualCostsViewModel ActualCosts { get; }
    public BudgetVarianceViewModel BudgetVariance { get; }
    public ForecastCostsViewModel ForecastCosts { get; }
    public WorkspaceSection SelectedSection { get => selectedSection; private set { if (selectedSection == value) return; selectedSection = value; OnPropertyChanged(); } }
    public bool CanNavigate => Companies.CanOpenRecovery && Projects.CanNavigate && Budgeting.CanNavigate && ActualCosts.CanNavigate && BudgetVariance.CanNavigate && ForecastCosts.CanNavigate;
    public bool CanOpenRecovery => CanNavigate;
    public async Task NavigateAsync(WorkspaceSection target, CancellationToken cancellationToken = default)
    {
        if (!CanNavigate || target == SelectedSection) return;
        if (target == WorkspaceSection.BusinessProjects)
        {
            try { await Projects.ReloadCompaniesAsync(cancellationToken); }
            catch { return; }
            if (!Projects.LastCompaniesReloadSucceeded) return;
        }
        if (target == WorkspaceSection.Budgeting)
        {
            await Budgeting.ReloadProjectsAsync(cancellationToken);
            if (!Budgeting.LastProjectsReloadSucceeded) return;
        }
        if (target == WorkspaceSection.ActualCosts)
        {
            await ActualCosts.ReloadProjectsAsync(cancellationToken);
            if (!ActualCosts.LastProjectsReloadSucceeded) return;
        }
        if (target == WorkspaceSection.ForecastCosts)
        {
            await ForecastCosts.ReloadProjectsAsync(cancellationToken);
            if (!ForecastCosts.LastProjectsReloadSucceeded) return;
        }
        if (target == WorkspaceSection.BudgetVariance)
        {
            await BudgetVariance.ReloadProjectsAsync(cancellationToken);
            if (!BudgetVariance.LastProjectsReloadSucceeded) return;
        }
        if (CanNavigate) SelectedSection = target;
    }
    private void ChildChanged(object? sender, PropertyChangedEventArgs e) { OnPropertyChanged(nameof(CanNavigate)); OnPropertyChanged(nameof(CanOpenRecovery)); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));

}
