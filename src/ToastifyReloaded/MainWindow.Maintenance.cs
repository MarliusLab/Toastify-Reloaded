using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using ToastifyReloaded.Models;

namespace ToastifyReloaded;

public partial class MainWindow
{
    private readonly bool _maintenanceTestMode;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _updateCancellation;
    private volatile bool _canCancelUpdate;
    private volatile bool _updateHandoffPending;
    private bool _exitAfterHandoffRequested;

    private string MaintenanceText(string english, string italian) =>
        _settings.ApplicationLanguage.Equals("Italian", StringComparison.OrdinalIgnoreCase) ? italian : english;

    private void UpdateMaintenanceButtons()
    {
        var busy = _updateCheckRunning || _maintenanceRunning;
        CheckUpdatesButton.IsEnabled = !busy;
        InstallUpdateButton.IsEnabled = !busy;
        CheckCompatibilityButton.IsEnabled = !busy;
        RepairSpotifyButton.IsEnabled = !busy;
    }

    private void CancelUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (!_canCancelUpdate)
            return;
        _updateCancellation?.Cancel();
        CancelUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = MaintenanceText("Canceling download…", "Annullamento del download…");
    }

    internal void ReportMaintenanceProgress(UpdateProgressInfo progress)
    {
        // This flag changes before posting UI work, closing the brief race
        // between download completion and the Windows elevation handoff.
        _canCancelUpdate = progress.CanCancel;
        _updateHandoffPending = progress.Phase == UpdatePhase.WaitingForPermission;
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;
        if (Dispatcher.CheckAccess())
            ApplyMaintenanceProgress(progress);
        else
            _ = Dispatcher.InvokeAsync(() => ApplyMaintenanceProgress(progress));
    }

    private void ApplyMaintenanceProgress(UpdateProgressInfo progress)
    {
        if (!_updateCheckRunning && !_maintenanceTestMode)
            return;
        UpdateProgressPanel.Visibility = Visibility.Visible;
        CancelUpdateButton.IsEnabled = progress.CanCancel;
        CancelUpdateButton.Visibility = progress.CanCancel ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgressBar.IsIndeterminate = progress.Phase is UpdatePhase.Verifying or UpdatePhase.WaitingForPermission or UpdatePhase.LaunchingUpdater ||
            (progress.Phase == UpdatePhase.Downloading && progress.Percent is null);
        if (progress.Phase != UpdatePhase.Downloading)
            UpdateProgressBar.Value = 100;
        else if (progress.Percent is { } percent)
            UpdateProgressBar.Value = percent;

        UpdateStatusText.Text = progress.Phase switch
        {
            UpdatePhase.Downloading when progress.IsPackageUpdate && progress.Percent is { } value => MaintenanceText(
                $"Downloading and preparing update: {value:0}%", $"Download e preparazione aggiornamento: {value:0}%"),
            UpdatePhase.Downloading when progress.IsPackageUpdate => MaintenanceText(
                "Downloading and preparing the update…", "Download e preparazione dell'aggiornamento…"),
            UpdatePhase.Downloading when progress.Percent is { } value => MaintenanceText(
                $"Downloading: {value:0}% — {FormatDownloadSize(progress.BytesReceived)} / {FormatDownloadSize(progress.TotalBytes!.Value)}",
                $"Download: {value:0}% — {FormatDownloadSize(progress.BytesReceived)} / {FormatDownloadSize(progress.TotalBytes!.Value)}"),
            UpdatePhase.Downloading => MaintenanceText(
                $"Downloading: {FormatDownloadSize(progress.BytesReceived)} received (total size unavailable)",
                $"Download: {FormatDownloadSize(progress.BytesReceived)} ricevuti (dimensione totale non disponibile)"),
            UpdatePhase.Verifying when progress.IsPackageUpdate => MaintenanceText(
                "Verifying the update package…", "Verifica del pacchetto di aggiornamento…"),
            UpdatePhase.Verifying => MaintenanceText(
                "Download complete. Verifying the installer…", "Download completato. Verifica dell'installer…"),
            UpdatePhase.WaitingForPermission when progress.IsPackageUpdate => MaintenanceText(
                "Preparing to apply the update. Confirm any Windows permission request.",
                "Preparazione dell'aggiornamento. Conferma l'eventuale richiesta di autorizzazione di Windows."),
            UpdatePhase.WaitingForPermission => MaintenanceText(
                "Download complete. Confirm the Windows permission request to install the update.",
                "Download completato. Conferma la richiesta di Windows per installare l'aggiornamento."),
            UpdatePhase.LaunchingUpdater => MaintenanceText(
                "Applying update. Toastify will restart with your existing settings.",
                "Applicazione dell'aggiornamento. Toastify verrà riavviato con le impostazioni esistenti."),
            _ => MaintenanceText(
                "Installation started. Follow the progress bar in the installer window.",
                "Installazione avviata. Segui la barra nella finestra dell'installer.")
        };
        AutomationProperties.SetName(UpdateProgressBar, UpdateStatusText.Text);
    }

    private static string FormatDownloadSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / (1024d * 1024):0.0} MB"
        : $"{bytes / 1024d:0.0} KB";

    private void FinishUpdateDisplay()
    {
        _canCancelUpdate = false;
        CancelUpdateButton.Visibility = Visibility.Collapsed;
        UpdateProgressBar.IsIndeterminate = false;
    }

    internal void ConfigureMaintenancePreview(string language, string theme)
    {
        if (!_maintenanceTestMode)
            throw new InvalidOperationException("Preview is only available to the isolated test window.");
        _settings.ApplicationLanguage = language;
        _settings.ApplicationTheme = theme;
    }

    internal bool CanCancelUpdate => _canCancelUpdate;

    internal Task ProbeMaintenancePreviewAsync()
    {
        if (!_maintenanceTestMode)
            throw new InvalidOperationException("Probe preview requires an isolated settings window.");
        return RunCompatibilityCheckAsync(automatic: false);
    }

    internal Task<bool> RunUpdatePreviewAsync()
    {
        if (!_maintenanceTestMode)
            throw new InvalidOperationException("Update preview requires an isolated settings window.");
        return CheckForToastifyUpdatesAsync(allowAutomaticInstall: true, forceInstall: true);
    }

    internal void RequestMaintenancePreviewExit()
    {
        if (!_maintenanceTestMode)
            throw new InvalidOperationException("Exit preview requires an isolated settings window.");
        ExitApplication();
    }

    private sealed class CallbackProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
