namespace MailClient.Services.Ai;

/// App-wide access point for the local AI service. Starts as a no-op; AiBootstrapper swaps in
/// a real implementation once the user has enabled AI and a model is on disk.
public static class Ai
{
    private static IAiService _service = new NullAiService();

    public static IAiService Service => _service;

    /// Set when the last attempt to load a model failed; null when things are fine.
    public static string? LastError { get; private set; }

    /// Raised on the thread that calls these - the bootstrapper marshals to the UI thread.
    public static event EventHandler? ReadyChanged;

    public static void SetService(IAiService service)
    {
        LastError = null;
        _service = service;
        ReadyChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void Fail(string error)
    {
        LastError = error;
        _service = new NullAiService();
        ReadyChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void Reset()
    {
        LastError = null;
        SetService(new NullAiService());
    }
}
