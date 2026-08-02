using System.ComponentModel;
using BusinessOS.Desktop.ViewModels;
using BusinessOS.Modules.BusinessProjects.Application;
using BusinessOS.Modules.Companies.Application;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace BusinessOS.Desktop;

public sealed partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly Action openRecovery;
    public MainViewModel Shell { get; }
    public MainWorkspaceViewModel Workspace { get; }
    public CompaniesViewModel Companies => Workspace.Companies;
    public BusinessProjectsViewModel Projects => Workspace.Projects;
    public IReadOnlyList<CompanyStatusValue> CompanyStatuses { get; } = Enum.GetValues<CompanyStatusValue>();
    public Visibility CompaniesVisibility => Workspace.SelectedSection == WorkspaceSection.Companies ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProjectsVisibility => Workspace.SelectedSection == WorkspaceSection.BusinessProjects ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CompanyEditorVisibility => Companies.IsEditorOpen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CompaniesEmptyVisibility => Companies.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProjectEditorVisibility => Projects.IsEditorOpen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProjectsEmptyVisibility => Projects.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    public DateTimeOffset? ProjectStartDate { get => new(Projects.PlannedStartDate.ToDateTime(TimeOnly.MinValue)); set { if (value is not null) Projects.PlannedStartDate = DateOnly.FromDateTime(value.Value.DateTime); } }
    public DateTimeOffset? ProjectOpeningDate { get => new(Projects.PlannedOpeningDate.ToDateTime(TimeOnly.MinValue)); set { if (value is not null) Projects.PlannedOpeningDate = DateOnly.FromDateTime(value.Value.DateTime); } }

    public MainWindow(MainViewModel shell, MainWorkspaceViewModel workspace, Action openRecovery)
    {
        InitializeComponent(); Shell = shell; Workspace = workspace; this.openRecovery = openRecovery;
        Workspace.PropertyChanged += Changed; Companies.PropertyChanged += Changed; Projects.PropertyChanged += Changed;
        if (Content is FrameworkElement root) root.DataContext = this;
        _ = RunUiOperationAsync(Companies.RefreshAsync, Companies.ReportPresentationFailure);
        _ = RunUiOperationAsync(Projects.InitializeAsync, Projects.ReportPresentationFailure);
    }

    private void Changed(object? sender, PropertyChangedEventArgs args)
    {
        foreach (var name in new[] { nameof(CompaniesVisibility), nameof(ProjectsVisibility), nameof(CompanyEditorVisibility), nameof(CompaniesEmptyVisibility), nameof(ProjectEditorVisibility), nameof(ProjectsEmptyVisibility), nameof(ProjectStartDate), nameof(ProjectOpeningDate) })
            PropertyChanged?.Invoke(this, new(name));
    }

    private async void CompaniesSection_Click(object sender, RoutedEventArgs e) => await RunUiOperationAsync(() => Workspace.NavigateAsync(WorkspaceSection.Companies), Projects.ReportPresentationFailure);
    private async void ProjectsSection_Click(object sender, RoutedEventArgs e) => await RunUiOperationAsync(() => Workspace.NavigateAsync(WorkspaceSection.BusinessProjects), Projects.ReportPresentationFailure);
    private void Recovery_Click(object sender, RoutedEventArgs e) { if (Workspace.CanOpenRecovery) openRecovery(); }
    private void Add_Click(object sender, RoutedEventArgs e) => Companies.BeginCreate();
    private async void Edit_Click(object sender, RoutedEventArgs e) => await RunUiOperationAsync(Companies.BeginEditAsync, Companies.ReportPresentationFailure);
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunUiOperationAsync(Companies.RefreshAsync, Companies.ReportPresentationFailure);
    private async void Save_Click(object sender, RoutedEventArgs e) => await RunUiOperationAsync(Companies.SaveAsync, Companies.ReportPresentationFailure);
    private void Cancel_Click(object sender, RoutedEventArgs e) => Companies.CancelEdit();
    private async void ProjectsRefresh_Click(object sender, RoutedEventArgs e) => await RunUiOperationAsync(Projects.RefreshAsync, Projects.ReportPresentationFailure);
    private void ProjectAdd_Click(object sender, RoutedEventArgs e) => Projects.BeginCreate();
    private async void ProjectEdit_Click(object sender, RoutedEventArgs e) => await RunUiOperationAsync(Projects.BeginEditAsync, Projects.ReportPresentationFailure);
    private async void ProjectSave_Click(object sender, RoutedEventArgs e) => await RunUiOperationAsync(Projects.SaveAsync, Projects.ReportPresentationFailure);
    private void ProjectCancel_Click(object sender, RoutedEventArgs e) => Projects.CancelEditor();
    private async void BusinessProjectsCompanySelector_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        await RunUiOperationAsync(() => Projects.SelectCompanyAsync((sender as ComboBox)?.SelectedItem as CompanyLookupItem), Projects.ReportPresentationFailure);
    private async void BusinessProjectsStatusFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if ((sender as ComboBox)?.SelectedItem is BusinessProjectStatusFilterOption option)
            await RunUiOperationAsync(() => Projects.SelectStatusFilterAsync(option), Projects.ReportPresentationFailure);
    }

    private async void Archive_Click(object sender, RoutedEventArgs e) => await RunUiOperationAsync(ArchiveCompanyDialogAsync, Companies.ReportPresentationFailure);
    private async Task ArchiveCompanyDialogAsync()
    {
        Companies.OpenArchiveDialog();
        if (!Companies.IsArchiveDialogOpen) return;
        try
        {
            var dialog = Dialog("ArchiveCompanyDialog", "Archiwizacja firmy", $"Czy zarchiwizować firmę {Companies.ArchivingCompanyName}?", "ConfirmArchiveCompanyButton", "CancelArchiveCompanyButton");
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) await Companies.ConfirmArchiveAsync();
            else Companies.CloseArchiveDialog();
        }
        finally { if (Companies.IsArchiveDialogOpen) Companies.CloseArchiveDialog(); }
    }

    private async void ProjectStatus_Click(object sender, RoutedEventArgs e) => await RunUiOperationAsync(ChangeProjectStatusDialogAsync, Projects.ReportPresentationFailure);
    private async Task ChangeProjectStatusDialogAsync()
    {
        await Projects.OpenStatusDialogAsync();
        if (!Projects.IsStatusDialogOpen) return;
        try
        {
            var selector = new ComboBox { ItemsSource = Projects.AllowedTransitions, SelectedItem = Projects.TargetStatus };
            AutomationProperties.SetAutomationId(selector, "BusinessProjectStatusSelector");
            var dialog = Dialog("BusinessProjectStatusDialog", "Zmiana statusu", selector, "ConfirmBusinessProjectStatusButton", "CancelBusinessProjectStatusButton");
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                Projects.TargetStatus = (BusinessProjectStatusValue?)selector.SelectedItem;
                await Projects.ConfirmStatusAsync();
            }
            else Projects.CloseStatusDialog();
        }
        finally { if (Projects.IsStatusDialogOpen) Projects.CloseStatusDialog(); }
    }

    private async void ProjectArchive_Click(object sender, RoutedEventArgs e) => await RunUiOperationAsync(ArchiveProjectDialogAsync, Projects.ReportPresentationFailure);
    private async Task ArchiveProjectDialogAsync()
    {
        Projects.OpenArchiveDialog();
        if (!Projects.IsArchiveDialogOpen) return;
        try
        {
            var dialog = Dialog("ArchiveBusinessProjectDialog", "Archiwizacja projektu", $"Czy zarchiwizować projekt {Projects.ArchivingProjectName}?", "ConfirmArchiveBusinessProjectButton", "CancelArchiveBusinessProjectButton");
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) await Projects.ConfirmArchiveAsync();
            else Projects.CloseArchiveDialog();
        }
        finally { if (Projects.IsArchiveDialogOpen) Projects.CloseArchiveDialog(); }
    }

    private ContentDialog Dialog(string id, string title, object content, string confirmId, string cancelId)
    {
        var confirm = new Style(typeof(Button)); confirm.Setters.Add(new Setter(AutomationProperties.AutomationIdProperty, confirmId));
        var cancel = new Style(typeof(Button)); cancel.Setters.Add(new Setter(AutomationProperties.AutomationIdProperty, cancelId));
        var dialog = new ContentDialog { Title = title, Content = content, PrimaryButtonText = "Potwierdź", CloseButtonText = "Anuluj", PrimaryButtonStyle = confirm, CloseButtonStyle = cancel, XamlRoot = Content.XamlRoot };
        AutomationProperties.SetAutomationId(dialog, id); return dialog;
    }

    private static async Task RunUiOperationAsync(Func<Task> operation, Action onFailure)
    {
        try { await operation(); }
        catch { onFailure(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
