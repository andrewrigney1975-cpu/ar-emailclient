namespace MailClient.Services.Ai;

/// App-wide access point for the local AI service. Starts as a no-op; AiBootstrapper swaps in
/// a real implementation once the user has enabled AI and a model is on disk.
public static class Ai
{
    private static IAiService _service = new NullAiService();

    public static IAiService Service => _service;

    /// Raised on the thread that calls SetService - the bootstrapper marshals to the UI thread.
    public static event EventHandler? ReadyChanged;

    public static void SetService(IAiService service)
    {
        _service = service;
        ReadyChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void Reset() => SetService(new NullAiService());
}
