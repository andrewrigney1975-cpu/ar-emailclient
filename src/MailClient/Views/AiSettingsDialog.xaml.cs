using MailClient.Services;
using MailClient.Services.Ai;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MailClient.Views;

public sealed partial class AiSettingsDialog : ContentDialog
{
    private CancellationTokenSource? _downloadCts;

    public AiSettingsDialog()
    {
        InitializeComponent();

        ModelBox.ItemsSource = AiModelManager.Catalog();
        var current = AiModelManager.Find(AppSettings.Current.AiModelId) ?? AiModelManager.Catalog().FirstOrDefault();
        ModelBox.SelectedItem = current;
        EnableToggle.IsOn = AppSettings.Current.AiEnabled;

        Ai.ReadyChanged += OnAiReadyChanged;
        Closed += (_, _) => Ai.ReadyChanged -= OnAiReadyChanged;

        RefreshStatus();
    }

    private void OnAiReadyChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(RefreshStatus);

    private AiModelInfo? Selected => ModelBox.SelectedItem as AiModelInfo;

    private void RefreshStatus()
    {
        var model = Selected;
        if (model is null)
        {
            StatusText.Text = "No models available.";
            return;
        }

        var installed = AiModelManager.IsInstalled(model.Id);
        var downloading = _downloadCts is not null;

        DownloadButton.IsEnabled = !installed && !downloading;
        RemoveButton.IsEnabled = installed && !downloading;
        CancelButton.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        DownloadButton.Visibility = downloading ? Visibility.Collapsed : Visibility.Visible;

        if (downloading)
        {
            return;
        }

        if (Ai.Service.IsReady)
        {
            StatusText.Text = $"Active — {Ai.Service.ModelName} ({Ai.Service.Backend}).";
        }
        else if (installed)
        {
            StatusText.Text = AppSettings.Current.AiEnabled
                ? "Downloaded. Loading…"
                : "Downloaded. Turn on \"Enable on-device AI\" to use it.";
        }
        else
        {
            StatusText.Text = $"Not downloaded — about {model.ApproxSize}, one time.";
        }
    }

    private void EnableToggle_Toggled(object sender, RoutedEventArgs e)
    {
        AppSettings.Update(s => s.AiEnabled = EnableToggle.IsOn);
        _ = AiBootstrapper.RefreshAsync(DispatcherQueue);
        RefreshStatus();
    }

    private void ModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Selected is { } model)
        {
            AppSettings.Update(s => s.AiModelId = model.Id);
            _ = AiBootstrapper.RefreshAsync(DispatcherQueue);
        }

        RefreshStatus();
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } model)
        {
            return;
        }

        Result.IsOpen = false;
        _downloadCts = new CancellationTokenSource();
        DownloadBar.Visibility = Visibility.Visible;
        DownloadBar.Value = 0;
        RefreshStatus();

        var progress = new Progress<AiDownloadProgress>(p =>
        {
            DownloadBar.Value = p.Fraction;
            StatusText.Text = $"Downloading {p.File} — {p.Fraction:P0}";
        });

        try
        {
            await AiModelManager.DownloadAsync(model, progress, _downloadCts.Token);
            await AiBootstrapper.RefreshAsync(DispatcherQueue);
        }
        catch (OperationCanceledException)
        {
            AiModelManager.Delete(model.Id);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("AiSettingsDialog.Download", ex);
            AiModelManager.Delete(model.Id);
            Result.Severity = InfoBarSeverity.Error;
            Result.Message = "Download failed: " + ex.Message;
            Result.IsOpen = true;
        }
        finally
        {
            _downloadCts?.Dispose();
            _downloadCts = null;
            DownloadBar.Visibility = Visibility.Collapsed;
            RefreshStatus();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _downloadCts?.Cancel();

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } model)
        {
            Ai.Reset();
            AiModelManager.Delete(model.Id);
            RefreshStatus();
        }
    }
}
