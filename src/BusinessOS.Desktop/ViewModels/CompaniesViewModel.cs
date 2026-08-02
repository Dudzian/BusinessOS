using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using BusinessOS.Modules.Companies.Application;

namespace BusinessOS.Desktop.ViewModels;

public sealed class CompaniesViewModel(ICompaniesCrudService service) : INotifyPropertyChanged
{
    private CompanyListItem? selectedCompany;
    private Guid? editingCompanyId;
    private long expectedVersion;
    private bool isBusy;
    private bool isEditorOpen;
    private bool isArchiveDialogOpen;
    private Guid? archivingCompanyId;
    private long archivingExpectedVersion;
    private string? archivingCompanyName;
    private string operationMessage = string.Empty;

    public ObservableCollection<CompanyListItem> Companies { get; } = [];
    public CompanyListItem? SelectedCompany
    {
        get => selectedCompany;
        set { if (IsBusy || IsArchiveDialogOpen) return; Set(ref selectedCompany, value); NotifyCapabilities(); }
    }
    public bool IsBusy { get => isBusy; private set { if (Set(ref isBusy, value)) NotifyCapabilities(); } }
    public bool IsEditorOpen { get => isEditorOpen; private set { if (Set(ref isEditorOpen, value)) NotifyCapabilities(); } }
    public bool IsArchiveDialogOpen { get => isArchiveDialogOpen; private set { if (Set(ref isArchiveDialogOpen, value)) NotifyCapabilities(); } }
    public string? ArchivingCompanyName => archivingCompanyName;
    public bool IsEmpty => Companies.Count == 0;
    public bool CanAdd => !IsBusy && !IsEditorOpen && !IsArchiveDialogOpen;
    public bool CanEdit => !IsBusy && !IsEditorOpen && !IsArchiveDialogOpen && SelectedCompany is not null;
    public bool CanArchive => CanEdit;
    public bool CanRefresh => !IsBusy && !IsEditorOpen && !IsArchiveDialogOpen;
    public bool CanSelectList => !IsBusy && !IsEditorOpen && !IsArchiveDialogOpen;
    public bool CanOpenRecovery => !IsBusy && !IsEditorOpen && !IsArchiveDialogOpen;
    public bool CanSave => !IsBusy && IsEditorOpen && !IsArchiveDialogOpen;
    public bool CanCancel => !IsBusy && IsEditorOpen && !IsArchiveDialogOpen;
    public string OperationMessage { get => operationMessage; private set => Set(ref operationMessage, value); }
    public string LegalName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string TaxId { get; set; } = string.Empty;
    public string CountryCode { get; set; } = "PL";
    public string BaseCurrency { get; set; } = "PLN";
    public string DefaultTimeZone { get; set; } = "Europe/Warsaw";
    public CompanyStatusValue Status { get; set; } = CompanyStatusValue.Active;

    public async Task RefreshAsync()
    {
        if (!CanRefresh) return;
        IsBusy = true;
        try { await ReloadCoreAsync(); }
        catch (OperationCanceledException) { OperationMessage = "Odświeżanie zostało anulowane."; }
        catch { OperationMessage = "Nie udało się odświeżyć listy firm."; }
        finally { IsBusy = false; }
    }

    public void BeginCreate()
    {
        if (!CanAdd) return;
        editingCompanyId = null; expectedVersion = 0; LegalName = DisplayName = TaxId = string.Empty;
        CountryCode = "PL"; BaseCurrency = "PLN"; DefaultTimeZone = "Europe/Warsaw"; Status = CompanyStatusValue.Active;
        IsEditorOpen = true; OperationMessage = string.Empty; NotifyEditor();
    }

    public async Task BeginEditAsync()
    {
        if (!CanEdit) return;
        var companyId = SelectedCompany!.Id;
        IsBusy = true;
        try
        {
            var details = await service.GetAsync(companyId, CancellationToken.None);
            if (details is null) { OperationMessage = "Nie znaleziono firmy."; await ReloadCoreAsync(); return; }
            editingCompanyId = companyId;
            PopulateEditor(details);
            IsEditorOpen = true;
        }
        catch (OperationCanceledException) { OperationMessage = "Otwieranie firmy zostało anulowane."; }
        catch { OperationMessage = "Nie udało się otworzyć danych firmy."; }
        finally { IsBusy = false; }
    }

    public void CancelEdit()
    {
        if (!CanCancel) return;
        IsEditorOpen = false; editingCompanyId = null; expectedVersion = 0;
    }

    public async Task SaveAsync()
    {
        if (!CanSave) return;
        IsBusy = true;
        try
        {
            var result = editingCompanyId is null
                ? await service.CreateAsync(new(LegalName, DisplayName, TaxId, CountryCode, BaseCurrency, DefaultTimeZone, Status), CancellationToken.None)
                : await service.UpdateAsync(new(editingCompanyId.Value, expectedVersion, LegalName, DisplayName, TaxId, CountryCode, BaseCurrency, DefaultTimeZone, Status), CancellationToken.None);
            OperationMessage = result.SafeMessage;
            if (result.Status == CompanyOperationStatus.Success)
            {
                IsEditorOpen = false; editingCompanyId = null; expectedVersion = 0;
                await ReloadCoreAsync();
                OperationMessage = result.SafeMessage;
            }
            else if (result.Status == CompanyOperationStatus.ConcurrencyConflict && editingCompanyId is { } id)
            {
                var current = await service.GetAsync(id, CancellationToken.None);
                if (current is null) { IsEditorOpen = false; editingCompanyId = null; }
                else PopulateEditor(current);
                OperationMessage = result.SafeMessage;
            }
        }
        catch (OperationCanceledException) { OperationMessage = "Zapisywanie zostało anulowane."; }
        catch { OperationMessage = "Nie udało się zapisać firmy. Spróbuj ponownie."; }
        finally { IsBusy = false; }
    }

    public void OpenArchiveDialog()
    {
        if (!CanArchive) return;
        archivingCompanyId = SelectedCompany!.Id;
        archivingExpectedVersion = SelectedCompany.Version;
        archivingCompanyName = SelectedCompany.DisplayName;
        OnPropertyChanged(nameof(ArchivingCompanyName));
        IsArchiveDialogOpen = true;
    }

    public void CloseArchiveDialog() { if (!IsBusy) ClearArchiveState(); }

    public async Task ConfirmArchiveAsync(CancellationToken cancellationToken = default)
    {
        if (!IsArchiveDialogOpen || IsBusy || archivingCompanyId is null) return;
        var capturedId = archivingCompanyId.Value;
        var capturedVersion = archivingExpectedVersion;
        IsBusy = true;
        try
        {
            var result = await service.ArchiveAsync(new(capturedId, capturedVersion), cancellationToken);
            OperationMessage = result.SafeMessage;
            if (result.Status is CompanyOperationStatus.Success or CompanyOperationStatus.ConcurrencyConflict or CompanyOperationStatus.NotFound)
                await ReloadCoreAsync();
            OperationMessage = result.SafeMessage;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { OperationMessage = "Archiwizacja została anulowana."; }
        catch { OperationMessage = "Nie udało się zarchiwizować firmy."; }
        finally { ClearArchiveState(); IsBusy = false; }
    }

    public Task ArchiveAsync() { OpenArchiveDialog(); return ConfirmArchiveAsync(); }

    public void ReportPresentationFailure() => OperationMessage = "Nie udało się wykonać operacji. Spróbuj ponownie.";

    private async Task ReloadCoreAsync()
    {
        var loaded = await service.ListAsync(CancellationToken.None);
        Companies.Clear();
        foreach (var company in loaded) Companies.Add(company);
        Set(ref selectedCompany, null, nameof(SelectedCompany));
        NotifyCapabilities();
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void PopulateEditor(CompanyDetails details)
    {
        LegalName = details.LegalName; DisplayName = details.DisplayName; TaxId = details.TaxIdentificationNumber ?? string.Empty;
        CountryCode = details.CountryCode; BaseCurrency = details.BaseCurrency; DefaultTimeZone = details.DefaultTimeZone;
        Status = details.Status; expectedVersion = details.Version; NotifyEditor();
    }

    private void NotifyEditor()
    {
        foreach (var property in new[] { nameof(LegalName), nameof(DisplayName), nameof(TaxId), nameof(CountryCode), nameof(BaseCurrency), nameof(DefaultTimeZone), nameof(Status) }) OnPropertyChanged(property);
    }
    private void NotifyCapabilities()
    {
        foreach (var property in new[] { nameof(CanAdd), nameof(CanEdit), nameof(CanArchive), nameof(CanRefresh), nameof(CanSelectList), nameof(CanOpenRecovery), nameof(CanSave), nameof(CanCancel) }) OnPropertyChanged(property);
    }
    private void ClearArchiveState()
    {
        IsArchiveDialogOpen = false;
        archivingCompanyId = null;
        archivingExpectedVersion = 0;
        archivingCompanyName = null;
        OnPropertyChanged(nameof(ArchivingCompanyName));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; OnPropertyChanged(name); return true;
    }
}
