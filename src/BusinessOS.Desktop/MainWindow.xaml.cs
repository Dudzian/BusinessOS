using System.ComponentModel;
using BusinessOS.Desktop.ViewModels;
using BusinessOS.Modules.BusinessProjects.Application;
using BusinessOS.Modules.Companies.Application;
using BusinessOS.Modules.Budgeting.Application;
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
    public BudgetingViewModel Budgeting => Workspace.Budgeting;
    public ActualCostsViewModel ActualCosts => Workspace.ActualCosts;
    public IReadOnlyList<CompanyStatusValue> CompanyStatuses { get; } = Enum.GetValues<CompanyStatusValue>();
    public Visibility CompaniesVisibility => Workspace.SelectedSection == WorkspaceSection.Companies ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProjectsVisibility => Workspace.SelectedSection == WorkspaceSection.BusinessProjects ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BudgetingVisibility => Workspace.SelectedSection == WorkspaceSection.Budgeting ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ActualCostsVisibility => Workspace.SelectedSection == WorkspaceSection.ActualCosts ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ActualCostsEmptyVisibility => ActualCosts.SelectedProject is not null && ActualCosts.Costs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ActualCostEditorVisibility => ActualCosts.IsEditorOpen ? Visibility.Visible : Visibility.Collapsed;
    public DateTimeOffset? ActualCostDate { get => new(ActualCosts.CostDate.ToDateTime(TimeOnly.MinValue)); set { if (value is not null) ActualCosts.CostDate = DateOnly.FromDateTime(value.Value.DateTime); } }
    public Visibility CompanyEditorVisibility => Companies.IsEditorOpen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CompaniesEmptyVisibility => Companies.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProjectEditorVisibility => Projects.IsEditorOpen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProjectsEmptyVisibility => Projects.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BudgetsEmptyVisibility => Budgeting.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BudgetEditorVisibility => Budgeting.IsBudgetEditorOpen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LineEditorVisibility => Budgeting.IsLineEditorOpen ? Visibility.Visible : Visibility.Collapsed;
    public DateTimeOffset? ProjectStartDate { get => new(Projects.PlannedStartDate.ToDateTime(TimeOnly.MinValue)); set { if (value is not null) Projects.PlannedStartDate = DateOnly.FromDateTime(value.Value.DateTime); } }
    public DateTimeOffset? ProjectOpeningDate { get => new(Projects.PlannedOpeningDate.ToDateTime(TimeOnly.MinValue)); set { if (value is not null) Projects.PlannedOpeningDate = DateOnly.FromDateTime(value.Value.DateTime); } }

    public MainWindow(MainViewModel shell, MainWorkspaceViewModel workspace, Action openRecovery)
    {
        InitializeComponent(); Shell = shell; Workspace = workspace; this.openRecovery = openRecovery;
        Workspace.PropertyChanged += Changed; Companies.PropertyChanged += Changed; Projects.PropertyChanged += Changed; Budgeting.PropertyChanged += Changed; ActualCosts.PropertyChanged += Changed;
        if (Content is FrameworkElement root) root.DataContext = this;
        _ = RunUiOperationAsync(Companies.RefreshAsync, Companies.ReportPresentationFailure);
        _ = RunUiOperationAsync(() => Projects.InitializeAsync(CancellationToken.None), Projects.ReportPresentationFailure);
    }

    private void Changed(object? sender, PropertyChangedEventArgs args)
    {
        foreach (var name in new[] { nameof(CompaniesVisibility), nameof(ProjectsVisibility), nameof(BudgetingVisibility), nameof(ActualCostsVisibility), nameof(ActualCostsEmptyVisibility), nameof(ActualCostEditorVisibility), nameof(ActualCostDate), nameof(CompanyEditorVisibility), nameof(CompaniesEmptyVisibility), nameof(ProjectEditorVisibility), nameof(ProjectsEmptyVisibility), nameof(BudgetsEmptyVisibility), nameof(BudgetEditorVisibility), nameof(LineEditorVisibility), nameof(ProjectStartDate), nameof(ProjectOpeningDate) })
            PropertyChanged?.Invoke(this, new(name));
    }

    private async void CompaniesSection_Click(object sender, RoutedEventArgs e) => await RunUiOperationAsync(() => Workspace.NavigateAsync(WorkspaceSection.Companies), Projects.ReportPresentationFailure);
    private async void ProjectsSection_Click(object sender, RoutedEventArgs e) => await RunUiOperationAsync(() => Workspace.NavigateAsync(WorkspaceSection.BusinessProjects), Projects.ReportPresentationFailure);
    private async void BudgetingSection_Click(object sender, RoutedEventArgs e) => await RunUiOperationAsync(() => Workspace.NavigateAsync(WorkspaceSection.Budgeting), Budgeting.ReportPresentationFailure);
    private async void ActualCostsSection_Click(object sender, RoutedEventArgs e) => await RunUiOperationAsync(() => Workspace.NavigateAsync(WorkspaceSection.ActualCosts), ActualCosts.ReportPresentationFailure);
    private async void ActualCostsProjectSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var addedProjects = e.AddedItems.OfType<BudgetProjectInfo>().ToArray();
        if (addedProjects.Length != 1) return;
        var project = addedProjects.Single();
        await RunUiOperationAsync(() => ActualCosts.SelectProjectAsync(project), ActualCosts.ReportPresentationFailure);
    }
    private async void ActualCostsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var addedCosts = e.AddedItems.OfType<ActualCostItem>().ToArray();
        if (addedCosts.Length != 1) return;
        var cost = addedCosts.Single();
        await RunUiOperationAsync(() => ActualCosts.SelectCostAsync(cost), ActualCosts.ReportPresentationFailure);
    }
    private async void ActualCostsRefresh_Click(object sender, RoutedEventArgs e) => await RunUiOperationAsync(() => ActualCosts.RefreshAsync(), ActualCosts.ReportPresentationFailure);
    private void ActualCostAdd_Click(object sender, RoutedEventArgs e) => ActualCosts.BeginAddCost();
    private void ActualCostEdit_Click(object sender, RoutedEventArgs e) => ActualCosts.BeginEditCost();
    private async void ActualCostSave_Click(object sender, RoutedEventArgs e) => await RunUiOperationAsync(() => ActualCosts.SaveCostAsync(), ActualCosts.ReportPresentationFailure);
    private void ActualCostCancel_Click(object sender, RoutedEventArgs e) => ActualCosts.CancelEditor();
    private async void ActualCostArchive_Click(object sender, RoutedEventArgs e) { ActualCosts.OpenArchiveDialog(); if (!ActualCosts.IsArchiveDialogOpen) return; var d = Dialog("ArchiveActualCostDialog", "Archiwizacja kosztu", "Czy zarchiwizować koszt?", "ConfirmArchiveActualCostButton", "CancelArchiveActualCostButton"); if (await d.ShowAsync() == ContentDialogResult.Primary) await ActualCosts.ConfirmArchiveAsync(); else ActualCosts.CancelArchive(); }
    private async void BudgetingProjectSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var addedProjects = e.AddedItems.OfType<BudgetProjectInfo>().ToArray();
        if (addedProjects.Length != 1) return;

        var project = addedProjects[0];
        await RunUiOperationAsync(() => Budgeting.SelectProjectAsync(project), Budgeting.ReportPresentationFailure);
    }
    private async void BudgetsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => await RunUiOperationAsync(() => Budgeting.SelectBudgetAsync((sender as ListView)?.SelectedItem as BudgetItem), Budgeting.ReportPresentationFailure);
    private async void BudgetVersionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var addedVersions = e.AddedItems.OfType<BudgetVersionItem>().ToArray();
        if (addedVersions.Length != 1) return;

        var version = addedVersions[0];
        await RunUiOperationAsync(() => Budgeting.SelectVersionAsync(version), Budgeting.ReportPresentationFailure);
    }
    private async void BudgetRefresh_Click(object sender, RoutedEventArgs e) => await RunUiOperationAsync(() => Budgeting.RefreshAsync(), Budgeting.ReportPresentationFailure);
    private void BudgetAdd_Click(object sender, RoutedEventArgs e) => Budgeting.BeginCreateBudget();
    private void BudgetRename_Click(object sender, RoutedEventArgs e) => Budgeting.BeginRenameBudget();
    private async void BudgetSave_Click(object sender, RoutedEventArgs e) => await RunUiOperationAsync(() => Budgeting.SaveBudgetAsync(), Budgeting.ReportPresentationFailure);
    private void BudgetCancel_Click(object sender, RoutedEventArgs e) => Budgeting.CancelBudgetEditor();
    private async void InitialVersion_Click(object sender, RoutedEventArgs e) => await RunUiOperationAsync(() => Budgeting.CreateInitialVersionAsync(), Budgeting.ReportPresentationFailure);
    private async void NextVersion_Click(object sender, RoutedEventArgs e) => await RunUiOperationAsync(() => Budgeting.CreateNextVersionAsync(), Budgeting.ReportPresentationFailure);
    private void LineAdd_Click(object sender, RoutedEventArgs e) => Budgeting.BeginAddLine();
    private void LineEdit_Click(object sender, RoutedEventArgs e) => Budgeting.BeginEditLine();
    private async void LineSave_Click(object sender, RoutedEventArgs e) => await RunUiOperationAsync(() => Budgeting.SaveLineAsync(), Budgeting.ReportPresentationFailure);
    private void LineCancel_Click(object sender, RoutedEventArgs e) => Budgeting.CancelLineEditor();
    private async void LineRemove_Click(object sender, RoutedEventArgs e) => await RunUiOperationAsync(() => Budgeting.RemoveSelectedLineAsync(), Budgeting.ReportPresentationFailure);
    private async void BudgetActivate_Click(object sender, RoutedEventArgs e) => await RunUiOperationAsync(() => BudgetLifecycleDialogAsync(true), Budgeting.ReportPresentationFailure);
    private async void BudgetArchive_Click(object sender, RoutedEventArgs e) => await RunUiOperationAsync(() => BudgetLifecycleDialogAsync(false), Budgeting.ReportPresentationFailure);
    private async Task BudgetLifecycleDialogAsync(bool activate)
    {
        var name = Budgeting.SelectedBudget?.Name; if (activate) Budgeting.OpenActivateDialog(); else Budgeting.OpenArchiveDialog(); if (!Budgeting.IsLifecycleDialogOpen) return;
        try
        {
            var dialog = Dialog(activate ? "ActivateBudgetDialog" : "ArchiveBudgetDialog", activate ? "Aktywacja budżetu" : "Archiwizacja budżetu", $"Czy {(activate ? "aktywować" : "zarchiwizować")} budżet {name}?", activate ? "ConfirmActivateBudgetButton" : "ConfirmArchiveBudgetButton", activate ? "CancelActivateBudgetButton" : "CancelArchiveBudgetButton");
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) { if (activate) await Budgeting.ActivateSelectedBudgetAsync(); else await Budgeting.ArchiveSelectedBudgetAsync(); } else Budgeting.CloseLifecycleDialog();
        }
        finally { if (Budgeting.IsLifecycleDialogOpen) Budgeting.CloseLifecycleDialog(); }
    }
    private void Recovery_Click(object sender, RoutedEventArgs e) { if (Workspace.CanOpenRecovery) openRecovery(); }
    private void Add_Click(object sender, RoutedEventArgs e) => Companies.BeginCreate();
    private async void Edit_Click(object sender, RoutedEventArgs e) => await RunUiOperationAsync(Companies.BeginEditAsync, Companies.ReportPresentationFailure);
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunUiOperationAsync(Companies.RefreshAsync, Companies.ReportPresentationFailure);
    private async void Save_Click(object sender, RoutedEventArgs e) => await RunUiOperationAsync(Companies.SaveAsync, Companies.ReportPresentationFailure);
    private void Cancel_Click(object sender, RoutedEventArgs e) => Companies.CancelEdit();
    private async void ProjectsRefresh_Click(object sender, RoutedEventArgs e) => await RunUiOperationAsync(() => Projects.RefreshAsync(CancellationToken.None), Projects.ReportPresentationFailure);
    private void ProjectAdd_Click(object sender, RoutedEventArgs e) => Projects.BeginCreate();
    private async void ProjectEdit_Click(object sender, RoutedEventArgs e) => await RunUiOperationAsync(() => Projects.BeginEditAsync(CancellationToken.None), Projects.ReportPresentationFailure);
    private async void ProjectSave_Click(object sender, RoutedEventArgs e) => await RunUiOperationAsync(() => Projects.SaveAsync(CancellationToken.None), Projects.ReportPresentationFailure);
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
