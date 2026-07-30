using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using BusinessOS.AppHost;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace BusinessOS.Desktop;

public sealed partial class DatabaseRecoveryWindow : Window
{
    private readonly ICompaniesRecoveryWorkflow workflow;
    private readonly Action back;
    private readonly Func<Task> close;
    private readonly Func<Task<ApplicationStartupResult>> prepareAfterRestore;
    private readonly Action<ApplicationStartupResult> transitionAfterRestore;
    private readonly ObservableCollection<RecoveryBackupItem> items = [];
    private readonly DeferredShutdownGate shutdownGate = new();
    private readonly RecoveryCloseIntent closeIntent = new();
    private CancellationTokenSource? operation;
    private Task currentOperationTask = Task.CompletedTask;
    private Task? closeTask;
    private bool isActive = true;
    private bool isBusy;
    private bool postRestoreProcessing;
    private int operationGate;

    public DatabaseRecoveryWindow(
        ICompaniesRecoveryWorkflow workflow,
        Action back,
        Func<Task> close,
        Func<Task<ApplicationStartupResult>> prepareAfterRestore,
        Action<ApplicationStartupResult> transitionAfterRestore)
    {
        InitializeComponent();
        this.workflow = workflow;
        this.back = back;
        this.close = close;
        this.prepareAfterRestore = prepareAfterRestore;
        this.transitionAfterRestore = transitionAfterRestore;
        BackupList.ItemsSource = items;
        AppWindow.Closing += OnAppWindowClosing;
        Closed += (_, _) =>
        {
            isActive = false;
        };
        Activated += OnActivated;
    }

    private async void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnActivated;
        currentOperationTask = LoadAsync();
        shutdownGate.Track(currentOperationTask);
        await currentOperationTask.ConfigureAwait(true);
    }

    private async Task LoadAsync()
    {
        if (Interlocked.Exchange(ref operationGate, 1) != 0) return;
        isBusy = true;
        SetBusy(true, "Wczytywanie kopii zapasowych…");
        using var source = new CancellationTokenSource();
        operation = source;
        try
        {
            var result = await workflow.LoadCatalogAsync(source.Token).ConfigureAwait(true);
            if (!CanUpdateUi()) return;
            items.Clear();
            foreach (var backup in result.Backups) items.Add(new RecoveryBackupItem(backup));
            EmptyText.Visibility = result.Succeeded && items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ErrorText.Text = result.Succeeded ? string.Empty : result.UserMessage;
            DiagnosticText.Text = result.DiagnosticId is null ? string.Empty : $"DiagnosticId: {result.DiagnosticId}";
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        finally
        {
            operation = null;
            isBusy = false;
            Interlocked.Exchange(ref operationGate, 0);
            if (CanUpdateUi()) SetBusy(false, string.Empty);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        currentOperationTask = LoadAsync();
        shutdownGate.Track(currentOperationTask);
        await currentOperationTask.ConfigureAwait(true);
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (BackupList.SelectedItem is not RecoveryBackupItem selected || !selected.IsRestorable || Interlocked.Exchange(ref operationGate, 1) != 0) return;
        try
        {
            if (!await ConfirmRestoreAsync(selected).ConfigureAwait(true) || !CanUpdateUi()) return;
            isBusy = true;
            SetBusy(true, "Trwa bezpieczne przywracanie bazy danych…");
            using var source = new CancellationTokenSource();
            operation = source;
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            currentOperationTask = RestoreCoreAsync(selected, source.Token, start.Task);
            shutdownGate.Track(currentOperationTask);
            start.SetResult();
            await currentOperationTask.ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Trace.TraceError("Recovery operation failed unexpectedly: {0}", exception);
        }
        finally
        {
            operation = null;
            isBusy = false;
            Interlocked.Exchange(ref operationGate, 0);
            if (CanUpdateUi()) SetBusy(false, StatusText.Text);
        }
    }

    private async Task RestoreCoreAsync(RecoveryBackupItem selected, CancellationToken cancellationToken, Task start)
    {
        await start.ConfigureAwait(true);
        try
        {
            var result = await workflow.RestoreAsync(selected.BackupId, cancellationToken).ConfigureAwait(true);
            if (!CanUpdateUi()) return;
            if (!result.Succeeded)
            {
                ErrorText.Text = result.UserMessage;
                DiagnosticText.Text = $"DiagnosticId: {result.DiagnosticId}";
                return;
            }
            StatusText.Text = "Kopia została przywrócona. Trwa przygotowanie bazy danych…";
            postRestoreProcessing = true;
            try
            {
                var startupResult = await prepareAfterRestore().ConfigureAwait(true);
                if (closeIntent.IsCloseRequested) return;
                transitionAfterRestore(startupResult);
            }
            finally
            {
                postRestoreProcessing = false;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task<bool> ConfirmRestoreAsync(RecoveryBackupItem selected)
    {
        var accepted = false;
        var confirm = new Button { Content = "Przywróć" };
        var cancel = new Button { Content = "Anuluj" };
        AutomationProperties.SetAutomationId(confirm, "ConfirmRestoreButton");
        AutomationProperties.SetAutomationId(cancel, "CancelRestoreButton");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        buttons.Children.Add(confirm); buttons.Children.Add(cancel);
        var content = new StackPanel { Spacing = 16 };
        content.Children.Add(new TextBlock { Text = $"Kopia: {selected.CreatedText}, {selected.SizeText}. Aktualna baza zostanie zastąpiona. BusinessOS spróbuje najpierw utworzyć kopię bezpieczeństwa.", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(buttons);
        var dialog = new ContentDialog { XamlRoot = (Content as FrameworkElement)?.XamlRoot, Title = "Przywrócić wybraną kopię?", Content = content };
        AutomationProperties.SetAutomationId(dialog, "ConfirmRestoreDialog");
        confirm.Click += (_, _) => { accepted = true; dialog.Hide(); };
        cancel.Click += (_, _) => dialog.Hide();
        await dialog.ShowAsync();
        return accepted;
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (closeIntent.IsInternalCloseAuthorized) return;
        args.Cancel = true;
        RequestClose();
    }

    private void RequestClose()
    {
        if (!closeIntent.RequestClose()) return;
        if (isActive) SetBusy(true, "Kończenie bezpiecznej operacji…");
        closeTask = ObserveCloseTaskAsync();
    }

    private async Task ObserveCloseTaskAsync()
    {
        try { await CloseWhenSafeAsync().ConfigureAwait(true); }
        catch (Exception exception) { Trace.TraceError("Recovery shutdown callback failed: {0}", exception); }
    }

    private async Task CloseWhenSafeAsync()
    {
        var result = await shutdownGate.WaitForSafeShutdownAsync(() => operation?.Cancel()).ConfigureAwait(true);
        if (result.OperationException is not null)
            Trace.TraceError("Recovery operation faulted while shutdown was pending: {0}", result.OperationException);
        if (result.CancellationException is not null)
            Trace.TraceError("Recovery cancellation callback failed while shutdown was pending: {0}", result.CancellationException);
        if (isActive && closeIntent.IsCloseRequested)
            await close().ConfigureAwait(true);
    }

    internal bool AuthorizeInternalClose() => closeIntent.TryAuthorizeInternalClose();

    private void BackupList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RestoreButton.IsEnabled = !isBusy && BackupList.SelectedItem is RecoveryBackupItem { IsRestorable: true };
    private void Back_Click(object sender, RoutedEventArgs e) { if (!isBusy && !closeIntent.IsCloseRequested) { back(); } }
    private void Close_Click(object sender, RoutedEventArgs e) => RequestClose();
    private bool CanUpdateUi() => isActive && !postRestoreProcessing && !closeIntent.IsInternalCloseAuthorized && !closeIntent.IsCloseRequested;
    private void SetBusy(bool value, string status)
    {
        BusyRing.IsActive = value;
        BackupList.IsEnabled = RefreshButton.IsEnabled = BackButton.IsEnabled = CloseButton.IsEnabled = !value;
        RestoreButton.IsEnabled = !value && BackupList.SelectedItem is RecoveryBackupItem { IsRestorable: true };
        StatusText.Text = status;
    }
}

public sealed class RecoveryBackupItem(CompaniesRecoveryBackup backup)
{
    public string BackupId { get; } = backup.BackupId;
    public bool IsRestorable { get; } = backup.IsRestorable;
    public string StatusText { get; } = backup.StatusText;
    public string CreatedText { get; } = backup.CreatedAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public string SizeText { get; } = FormatSize(backup.SizeBytes);
    public string AutomationName { get; } = $"Kopia zapasowa z {backup.CreatedAtUtc.ToLocalTime().ToString("f", CultureInfo.CurrentCulture)}, {(backup.IsRestorable ? "prawidłowa" : "nieprawidłowa")}";

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)bytes; var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value.ToString(unit == 0 ? "N0" : "N1", CultureInfo.CurrentCulture)} {units[unit]}";
    }
}
