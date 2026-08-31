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

        var service = await Task.Run(() =>
            OnnxGenAiService.TryCreate(AiModelManager.Dir(info.Id), info.Backend, info.DisplayName));

        dispatcher.TryEnqueue(() =>
        {
            if (service is not null)
            {
                Ai.SetService(service);
            }
            else
            {
                Ai.Reset();
            }
        });
    }
}
