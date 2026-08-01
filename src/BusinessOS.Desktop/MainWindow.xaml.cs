using System.ComponentModel;
using BusinessOS.Desktop.ViewModels;
using BusinessOS.Modules.Companies.Application;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

namespace BusinessOS.Desktop;

public sealed partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly Action openRecovery;
    public MainViewModel Shell { get; }
    public CompaniesViewModel Companies { get; }
    public IReadOnlyList<CompanyStatusValue> Statuses { get; } = Enum.GetValues<CompanyStatusValue>();
    public Visibility EditorVisibility => Companies.IsEditorOpen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyVisibility => Companies.IsEmpty ? Visibility.Visible : Visibility.Collapsed;

    public MainWindow(MainViewModel shell, CompaniesViewModel companies, Action openRecovery)
    {
        InitializeComponent(); Title = "BusinessOS"; Shell = shell; Companies = companies; this.openRecovery = openRecovery;
        Companies.PropertyChanged += (_, _) => { PropertyChanged?.Invoke(this, new(nameof(EditorVisibility))); PropertyChanged?.Invoke(this, new(nameof(EmptyVisibility))); };
        if (Content is FrameworkElement root) root.DataContext = this;
        _ = Companies.RefreshAsync();
    }

    private void Recovery_Click(object sender, RoutedEventArgs e)
    {
        if (Companies.CanOpenRecovery) openRecovery();
    }
    private void Add_Click(object sender, RoutedEventArgs e) => Companies.BeginCreate();
    private async void Edit_Click(object sender, RoutedEventArgs e) => await Companies.BeginEditAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await Companies.RefreshAsync();
    private async void Save_Click(object sender, RoutedEventArgs e) => await Companies.SaveAsync();
    private void Cancel_Click(object sender, RoutedEventArgs e) => Companies.CancelEdit();
    private async void Archive_Click(object sender, RoutedEventArgs e)
    {
        if (Companies.SelectedCompany is null) return;
        var confirmStyle = new Style(typeof(Button));
        confirmStyle.Setters.Add(new Setter(AutomationProperties.AutomationIdProperty, "ConfirmArchiveCompanyButton"));
        var cancelStyle = new Style(typeof(Button));
        cancelStyle.Setters.Add(new Setter(AutomationProperties.AutomationIdProperty, "CancelArchiveCompanyButton"));
        var dialog = new ContentDialog
        {
            Title = "Archiwizacja firmy",
            Content = $"Czy zarchiwizować firmę {Companies.SelectedCompany.DisplayName}?",
            PrimaryButtonText = "Archiwizuj",
            CloseButtonText = "Anuluj",
            PrimaryButtonStyle = confirmStyle,
            CloseButtonStyle = cancelStyle,
            XamlRoot = Content.XamlRoot,
        };
        AutomationProperties.SetAutomationId(dialog, "ArchiveCompanyDialog");
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await Companies.ArchiveAsync();
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
