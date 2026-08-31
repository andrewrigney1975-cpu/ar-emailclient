using Microsoft.UI.Dispatching;

namespace MailClient.Services.Ai;

/// Rebuilds Ai.Service from current settings: loads the ONNX model when AI is enabled and a
/// model is installed, otherwise falls back to the no-op service. Call at startup and whenever
/// the AI settings change.
public static class AiBootstrapper
{
    public static async Task RefreshAsync(DispatcherQueue dispatcher)
    {
        var settings = AppSettings.Current;

        if (!settings.AiEnabled ||
            AiModelManager.Find(settings.AiModelId) is not { } info ||
            !AiModelManager.IsInstalled(info.Id))
        {
            dispatcher.TryEnqueue(Ai.Reset);
            return;
        }

        OnnxGenAiService? service = null;
        string? error = null;
        try
        {
            service = await Task.Run(() =>
                OnnxGenAiService.Create(AiModelManager.Dir(info.Id), info.Backend, info.DisplayName));
        }
        catch (Exception ex)
        {
            LoggingService.Warn("AiBootstrapper.RefreshAsync", ex);
            error = ex.Message.Split('\n', '\r').FirstOrDefault()?.Trim() ?? ex.Message;
        }

        dispatcher.TryEnqueue(() =>
        {
            if (service is not null)
            {
                Ai.SetService(service);
            }
            else if (error is not null)
            {
                Ai.Fail(error);
            }
            else
            {
                Ai.Reset();
            }
        });
    }
}
