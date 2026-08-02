using System.ComponentModel;
using System.Runtime.CompilerServices;
namespace BusinessOS.Desktop.ViewModels;

public enum WorkspaceSection { Companies, BusinessProjects }
public sealed class MainWorkspaceViewModel : INotifyPropertyChanged
{
    private WorkspaceSection selectedSection;
    public MainWorkspaceViewModel(CompaniesViewModel companies, BusinessProjectsViewModel projects)
    {
        Companies = companies; Projects = projects;
        Companies.PropertyChanged += ChildChanged; Projects.PropertyChanged += ChildChanged;
    }
    public CompaniesViewModel Companies { get; }
    public BusinessProjectsViewModel Projects { get; }
    public WorkspaceSection SelectedSection { get => selectedSection; private set { if (selectedSection == value) return; selectedSection = value; OnPropertyChanged(); } }
    public bool CanNavigate => Companies.CanOpenRecovery && Projects.CanNavigate;
    public bool CanOpenRecovery => Companies.CanOpenRecovery && Projects.CanNavigate;
    public async Task NavigateAsync(WorkspaceSection target, CancellationToken cancellationToken = default)
    {
        if (!CanNavigate || target == SelectedSection) return;
        if (target == WorkspaceSection.BusinessProjects)
        {
            try { await Projects.ReloadCompaniesAsync(cancellationToken); }
            catch { return; }
            if (!Projects.LastCompaniesReloadSucceeded) return;
        }
        if (CanNavigate) SelectedSection = target;
    }
    private void ChildChanged(object? sender, PropertyChangedEventArgs e) { OnPropertyChanged(nameof(CanNavigate)); OnPropertyChanged(nameof(CanOpenRecovery)); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
