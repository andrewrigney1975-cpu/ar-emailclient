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
            var dir = AiModelManager.Dir(info.Id);
            service = await Task.Run(() =>
            {
                try
                {
                    return OnnxGenAiService.Create(dir, info.Backend, info.DisplayName);
                }
                catch (Exception dmlEx) when (info.Backend == AiBackend.DirectMl)
                {
                    // Fall back to CPU so the feature still works if DirectML rejects the model.
                    LoggingService.Warn("AiBootstrapper.RefreshAsync (DirectML, falling back to CPU)", dmlEx);
                    return OnnxGenAiService.Create(dir, AiBackend.Cpu, info.DisplayName + " (CPU fallback)");
                }
            });
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
