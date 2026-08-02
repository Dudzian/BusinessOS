using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using BusinessOS.Modules.BusinessProjects.Application;
using BusinessOS.Modules.Companies.Application;

namespace BusinessOS.Desktop.ViewModels;

public sealed record BusinessProjectStatusFilterOption(string DisplayName, BusinessProjectStatusValue? Value);

public sealed class BusinessProjectsViewModel : INotifyPropertyChanged
{
    private CompanyLookupItem? selectedCompany;
    private BusinessProjectListItem? selectedProject;
    private Guid? editingProjectId;
    private long expectedVersion;
    private Guid? statusProjectId;
    private long statusExpectedVersion;
    private Guid? archivingProjectId;
    private long archivingExpectedVersion;
    private string? archivingProjectName;
    private bool isBusy;
    private bool isEditorOpen;
    private bool isStatusDialogOpen;
    private bool isArchiveDialogOpen;
    private BusinessProjectStatusFilterOption selectedStatusFilter;
    private string operationMessage = string.Empty;

    public BusinessProjectsViewModel(IBusinessProjectsCrudService projects, ICompaniesLookupService companies, TimeProvider timeProvider)
    {
        this.projects = projects;
        this.companies = companies;
        this.timeProvider = timeProvider;
        StatusFilters = [new("Wszystkie", null), .. Enum.GetValues<BusinessProjectStatusValue>().Select(status => new BusinessProjectStatusFilterOption(status.ToString(), status))];
        selectedStatusFilter = StatusFilters[0];
    }

    private readonly IBusinessProjectsCrudService projects;
    private readonly ICompaniesLookupService companies;
    private readonly TimeProvider timeProvider;
    public ObservableCollection<CompanyLookupItem> Companies { get; } = [];
    public ObservableCollection<BusinessProjectListItem> Projects { get; } = [];
    public ObservableCollection<BusinessProjectStatusValue> AllowedTransitions { get; } = [];
    public IReadOnlyList<BusinessProjectStatusFilterOption> StatusFilters { get; }

    public CompanyLookupItem? SelectedCompany => selectedCompany;
    public BusinessProjectListItem? SelectedProject
    {
        get => selectedProject;
        set
        {
            if (!CanSelectProject || selectedProject?.Id == value?.Id) return;
            selectedProject = value;
            OnPropertyChanged();
            NotifyCapabilities();
        }
    }
    public BusinessProjectStatusFilterOption SelectedStatusFilter => selectedStatusFilter;
    public bool IsBusy { get => isBusy; private set { if (Set(ref isBusy, value)) NotifyCapabilities(); } }
    public bool IsEditorOpen { get => isEditorOpen; private set { if (Set(ref isEditorOpen, value)) NotifyCapabilities(); } }
    public bool IsStatusDialogOpen { get => isStatusDialogOpen; private set { if (Set(ref isStatusDialogOpen, value)) NotifyCapabilities(); } }
    public bool IsArchiveDialogOpen { get => isArchiveDialogOpen; private set { if (Set(ref isArchiveDialogOpen, value)) NotifyCapabilities(); } }
    public bool HasOpenInteraction => IsEditorOpen || IsStatusDialogOpen || IsArchiveDialogOpen;
    public bool IsEmpty => SelectedCompany is not null && Projects.Count == 0;
    public bool LastCompaniesReloadSucceeded { get; private set; }
    public bool CanSelectCompany => !IsBusy && !HasOpenInteraction;
    public bool CanSelectProject => !IsBusy && !HasOpenInteraction;
    public bool CanChangeFilter => !IsBusy && !HasOpenInteraction;
    public bool CanAdd => !IsBusy && !HasOpenInteraction && SelectedCompany is not null;
    public bool CanEdit => CanAdd && SelectedProject is not null;
    public bool CanChangeStatus => CanEdit && SelectedProject!.Status is not (BusinessProjectStatusValue.Closed or BusinessProjectStatusValue.Cancelled);
    public bool CanArchive => CanEdit;
    public bool CanRefresh => !IsBusy && !HasOpenInteraction;
    public bool CanSave => IsEditorOpen && !IsBusy;
    public bool CanCancel => IsEditorOpen && !IsBusy;
    public bool CanNavigate => !IsBusy && !HasOpenInteraction;
    public string OperationMessage { get => operationMessage; private set => Set(ref operationMessage, value); }
    public string? ArchivingProjectName => archivingProjectName;
    public string Name { get; set; } = string.Empty;
    public string BusinessType { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateOnly PlannedStartDate { get; set; }
    public DateOnly PlannedOpeningDate { get; set; }
    public string BaseCurrency { get; set; } = string.Empty;
    public BusinessProjectStatusValue? TargetStatus { get; set; }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => ReloadCompaniesAsync(cancellationToken);

    public async Task ReloadCompaniesAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy || HasOpenInteraction) { LastCompaniesReloadSucceeded = false; return; }
        IsBusy = true;
        LastCompaniesReloadSucceeded = false;
        var previousId = selectedCompany?.Id;
        var hadPreviousSelection = previousId.HasValue;
        try
        {
            var loaded = await companies.ListActiveAsync(cancellationToken);
            Companies.Clear();
            foreach (var company in loaded) Companies.Add(company);
            selectedCompany = previousId is { } id ? Companies.FirstOrDefault(company => company.Id == id) : null;
            if (selectedCompany is null && !hadPreviousSelection && Companies.Count == 1) selectedCompany = Companies[0];
            OnPropertyChanged(nameof(SelectedCompany));
            if (selectedCompany is null)
            {
                ClearAllInteractionState();
                ClearProjects();
                OperationMessage = hadPreviousSelection
                    ? "Wybrana firma nie jest już dostępna. Wybierz inną firmę."
                    : Companies.Count == 0 ? "Najpierw dodaj aktywną firmę." : string.Empty;
            }
            else await ReloadCoreAsync(cancellationToken);
            LastCompaniesReloadSucceeded = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { OperationMessage = "Odświeżanie firm zostało anulowane."; }
        catch (CompaniesLookupException) { OperationMessage = "Nie udało się załadować aktywnych firm."; }
        catch { OperationMessage = "Nie udało się załadować aktywnych firm."; }
        finally { IsBusy = false; OnPropertyChanged(nameof(LastCompaniesReloadSucceeded)); }
    }

    public async Task SelectCompanyAsync(CompanyLookupItem? company, CancellationToken cancellationToken = default)
    {
        if (!CanSelectCompany || selectedCompany?.Id == company?.Id) return;
        if (company is null)
        {
            selectedCompany = null;
            OnPropertyChanged(nameof(SelectedCompany));
            ClearAllInteractionState();
            ClearProjects();
            return;
        }
        IsBusy = true;
        try
        {
            var loaded = await LoadProjectsAsync(company.Id, selectedStatusFilter.Value, cancellationToken);
            selectedCompany = company;
            OnPropertyChanged(nameof(SelectedCompany));
            ClearAllInteractionState();
            ReplaceProjects(loaded);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { OnPropertyChanged(nameof(SelectedCompany)); OperationMessage = "Wybór firmy został anulowany."; }
        catch (BusinessProjectsReadException) { OnPropertyChanged(nameof(SelectedCompany)); OperationMessage = "Nie udało się załadować projektów firmy."; }
        catch { OnPropertyChanged(nameof(SelectedCompany)); OperationMessage = "Nie udało się załadować projektów firmy."; }
        finally { IsBusy = false; }
    }

    public async Task SelectStatusFilterAsync(BusinessProjectStatusFilterOption option, CancellationToken cancellationToken = default)
    {
        if (!CanChangeFilter || option is null || ReferenceEquals(option, selectedStatusFilter)) return;
        IsBusy = true;
        try
        {
            var loaded = selectedCompany is null
                ? Array.Empty<BusinessProjectListItem>()
                : await LoadProjectsAsync(selectedCompany.Id, option.Value, cancellationToken);
            selectedStatusFilter = option;
            OnPropertyChanged(nameof(SelectedStatusFilter));
            ReplaceProjects(loaded);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { OnPropertyChanged(nameof(SelectedStatusFilter)); OperationMessage = "Zmiana filtra została anulowana."; }
        catch (BusinessProjectsReadException) { OnPropertyChanged(nameof(SelectedStatusFilter)); OperationMessage = "Nie udało się zastosować filtra projektów."; }
        catch { OnPropertyChanged(nameof(SelectedStatusFilter)); OperationMessage = "Nie udało się zastosować filtra projektów."; }
        finally { IsBusy = false; }
    }

    public void ReportPresentationFailure() => OperationMessage = "Nie udało się wykonać operacji. Spróbuj ponownie.";

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRefresh) return;
        IsBusy = true;
        try { await ReloadCoreAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { OperationMessage = "Odświeżanie zostało anulowane."; }
        catch (BusinessProjectsReadException) { OperationMessage = "Nie udało się odświeżyć projektów."; }
        catch { OperationMessage = "Nie udało się odświeżyć projektów."; }
        finally { IsBusy = false; }
    }

    public void BeginCreate()
    {
        if (!CanAdd) return;
        editingProjectId = null; expectedVersion = 0; Name = BusinessType = Location = Description = string.Empty;
        var today = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
        PlannedStartDate = PlannedOpeningDate = today; BaseCurrency = SelectedCompany!.BaseCurrency;
        IsEditorOpen = true; NotifyEditor();
    }

    public async Task BeginEditAsync(CancellationToken cancellationToken = default)
    {
        if (!CanEdit) return;
        var capturedId = SelectedProject!.Id;
        IsBusy = true;
        try
        {
            var details = await projects.GetAsync(capturedId, cancellationToken);
            if (details is null) { OperationMessage = "Nie znaleziono projektu."; await ReloadCoreAsync(cancellationToken); return; }
            editingProjectId = capturedId; Populate(details); IsEditorOpen = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { OperationMessage = "Otwieranie projektu zostało anulowane."; }
        catch (BusinessProjectsReadException) { OperationMessage = "Nie udało się otworzyć projektu."; }
        catch { OperationMessage = "Nie udało się otworzyć projektu."; }
        finally { IsBusy = false; }
    }

    public void CancelEditor() { if (!IsBusy) CloseEditorState(); }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (!CanSave) return;
        IsBusy = true;
        try
        {
            var result = editingProjectId is null
                ? await projects.CreateAsync(new(SelectedCompany!.Id, Name, BusinessType, Location, Description, PlannedStartDate, PlannedOpeningDate, BaseCurrency), cancellationToken)
                : await projects.UpdateAsync(new(editingProjectId.Value, expectedVersion, Name, BusinessType, Location, Description, PlannedStartDate, PlannedOpeningDate, BaseCurrency), cancellationToken);
            OperationMessage = result.SafeMessage;
            if (result.Status == BusinessProjectOperationStatus.Success) { CloseEditorState(); await ReloadCoreAsync(cancellationToken); OperationMessage = result.SafeMessage; }
            else if (result.Status == BusinessProjectOperationStatus.ConcurrencyConflict && editingProjectId is { } id)
            {
                var current = await projects.GetAsync(id, cancellationToken);
                if (current is null) CloseEditorState(); else Populate(current);
                OperationMessage = result.SafeMessage;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { OperationMessage = "Zapisywanie zostało anulowane."; }
        catch (BusinessProjectsReadException) { OperationMessage = "Nie udało się odświeżyć projektu po konflikcie."; }
        catch { OperationMessage = "Nie udało się zapisać projektu."; }
        finally { IsBusy = false; }
    }

    public async Task OpenStatusDialogAsync(CancellationToken cancellationToken = default)
    {
        if (!CanChangeStatus) { OperationMessage = "Projekt nie ma dostępnych zmian statusu."; return; }
        var capturedId = SelectedProject!.Id;
        IsBusy = true;
        try
        {
            var details = await projects.GetAsync(capturedId, cancellationToken);
            AllowedTransitions.Clear();
            if (details is null) { OperationMessage = "Nie znaleziono projektu."; await ReloadCoreAsync(cancellationToken); return; }
            foreach (var status in details.AllowedTransitions) AllowedTransitions.Add(status);
            if (AllowedTransitions.Count == 0) { OperationMessage = "Projekt nie ma dostępnych zmian statusu."; return; }
            statusProjectId = details.Id; statusExpectedVersion = details.Version;
            TargetStatus = AllowedTransitions[0]; IsStatusDialogOpen = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { OperationMessage = "Otwieranie zmiany statusu zostało anulowane."; }
        catch (BusinessProjectsReadException) { OperationMessage = "Nie udało się pobrać dozwolonych zmian statusu."; }
        catch { OperationMessage = "Nie udało się pobrać dozwolonych zmian statusu."; }
        finally { IsBusy = false; NotifyCapabilities(); }
    }

    public void CloseStatusDialog() { if (!IsBusy) ClearStatusState(); }

    public async Task ConfirmStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!IsStatusDialogOpen || IsBusy || statusProjectId is null || TargetStatus is null) return;
        IsBusy = true;
        try
        {
            var result = await projects.ChangeStatusAsync(new(statusProjectId.Value, statusExpectedVersion, TargetStatus.Value), cancellationToken);
            OperationMessage = result.SafeMessage;
            ClearStatusState();
            await ReloadCoreAsync(cancellationToken);
            OperationMessage = result.SafeMessage;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { OperationMessage = "Zmiana statusu została anulowana."; }
        catch (BusinessProjectsReadException) { OperationMessage = "Nie udało się odświeżyć projektu po zmianie statusu."; }
        catch { OperationMessage = "Nie udało się zmienić statusu projektu."; }
        finally { ClearStatusState(); IsBusy = false; }
    }

    public void OpenArchiveDialog()
    {
        if (!CanArchive) return;
        archivingProjectId = SelectedProject!.Id;
        archivingExpectedVersion = SelectedProject.Version;
        archivingProjectName = SelectedProject.Name;
        OnPropertyChanged(nameof(ArchivingProjectName));
        IsArchiveDialogOpen = true;
    }

    public void CloseArchiveDialog() { if (!IsBusy) ClearArchiveState(); }

    public async Task ConfirmArchiveAsync(CancellationToken cancellationToken = default)
    {
        if (!IsArchiveDialogOpen || IsBusy || archivingProjectId is null) return;
        var capturedId = archivingProjectId.Value;
        var capturedVersion = archivingExpectedVersion;
        IsBusy = true;
        try
        {
            var result = await projects.ArchiveAsync(new(capturedId, capturedVersion), cancellationToken);
            OperationMessage = result.SafeMessage;
            await ReloadCoreAsync(cancellationToken);
            OperationMessage = result.SafeMessage;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { OperationMessage = "Archiwizacja została anulowana."; }
        catch (BusinessProjectsReadException) { OperationMessage = "Nie udało się odświeżyć projektów po archiwizacji."; }
        catch { OperationMessage = "Nie udało się zarchiwizować projektu."; }
        finally { ClearArchiveState(); IsBusy = false; }
    }

    private async Task ReloadCoreAsync(CancellationToken cancellationToken)
    {
        if (SelectedCompany is null) { ClearProjects(); return; }
        var loaded = await LoadProjectsAsync(SelectedCompany.Id, SelectedStatusFilter.Value, cancellationToken);
        ReplaceProjects(loaded);
    }

    private Task<IReadOnlyList<BusinessProjectListItem>> LoadProjectsAsync(Guid companyId, BusinessProjectStatusValue? status, CancellationToken cancellationToken) =>
        projects.ListAsync(companyId, status, cancellationToken);

    private void ReplaceProjects(IReadOnlyList<BusinessProjectListItem> loaded)
    {
        Projects.Clear(); foreach (var project in loaded) Projects.Add(project);
        selectedProject = null; AllowedTransitions.Clear();
        OnPropertyChanged(nameof(SelectedProject)); OnPropertyChanged(nameof(IsEmpty)); NotifyCapabilities();
    }

    private void ClearProjects() { Projects.Clear(); selectedProject = null; AllowedTransitions.Clear(); OnPropertyChanged(nameof(SelectedProject)); OnPropertyChanged(nameof(IsEmpty)); NotifyCapabilities(); }
    private void CloseEditorState() { IsEditorOpen = false; editingProjectId = null; expectedVersion = 0; }
    private void ClearAllInteractionState() { CloseEditorState(); ClearStatusState(); ClearArchiveState(); }
    private void ClearStatusState() { IsStatusDialogOpen = false; statusProjectId = null; statusExpectedVersion = 0; TargetStatus = null; AllowedTransitions.Clear(); }
    private void ClearArchiveState() { IsArchiveDialogOpen = false; archivingProjectId = null; archivingExpectedVersion = 0; archivingProjectName = null; OnPropertyChanged(nameof(ArchivingProjectName)); }
    private void Populate(BusinessProjectDetails details) { Name = details.Name; BusinessType = details.BusinessType; Location = details.Location; Description = details.Description; PlannedStartDate = details.PlannedStartDate; PlannedOpeningDate = details.PlannedOpeningDate; BaseCurrency = details.BaseCurrency; expectedVersion = details.Version; editingProjectId = details.Id; NotifyEditor(); }
    private void NotifyEditor() { foreach (var name in new[] { nameof(Name), nameof(BusinessType), nameof(Location), nameof(Description), nameof(PlannedStartDate), nameof(PlannedOpeningDate), nameof(BaseCurrency) }) OnPropertyChanged(name); }
    private void NotifyCapabilities() { foreach (var name in new[] { nameof(CanSelectCompany), nameof(CanSelectProject), nameof(CanChangeFilter), nameof(CanAdd), nameof(CanEdit), nameof(CanChangeStatus), nameof(CanArchive), nameof(CanRefresh), nameof(CanSave), nameof(CanCancel), nameof(CanNavigate), nameof(HasOpenInteraction), nameof(IsEmpty) }) OnPropertyChanged(name); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; OnPropertyChanged(name); return true; }
}
