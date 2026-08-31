namespace MailClient.Services.Ai;

public enum AiBackend
{
    None,
    Cpu,
    DirectMl,
    PhiSilica,
}

/// One request to the local model. Kept deliberately small - the models we run are
/// prompt-sensitive, so callers should pass tight system/user text.
public sealed class AiPrompt
{
    public string System { get; init; } = string.Empty;
    public required string User { get; init; }
    public int MaxTokens { get; init; } = 400;
    public float Temperature { get; init; } = 0.3f;
}

/// Everything AI in the app goes through this. Implementations run entirely on the local
/// device - nothing is ever sent over the network.
public interface IAiService
{
    /// True when a model is loaded and calls will succeed.
    bool IsReady { get; }

    AiBackend Backend { get; }

    /// Human-readable model name for the UI ("Phi-3.5-mini", "Phi Silica", …).
    string ModelName { get; }

    /// Streams the completion token-by-token.
    IAsyncEnumerable<string> StreamAsync(AiPrompt prompt, CancellationToken ct);

    /// Runs the completion to the end and returns the whole string.
    Task<string> CompleteAsync(AiPrompt prompt, CancellationToken ct);

    /// Runs the completion and deserialises the (first) JSON object in the output to T.
    /// Returns default on failure.
    Task<T?> CompleteJsonAsync<T>(AiPrompt prompt, CancellationToken ct);
}
