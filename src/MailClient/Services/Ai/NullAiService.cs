using System.Runtime.CompilerServices;

namespace MailClient.Services.Ai;

/// Used when AI is disabled or no model is available. Every feature checks IsReady first,
/// so these members should never actually run - they fail safe if they do.
public sealed class NullAiService : IAiService
{
    public bool IsReady => false;

    public AiBackend Backend => AiBackend.None;

    public string ModelName => "(off)";

    public async IAsyncEnumerable<string> StreamAsync(AiPrompt prompt, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        yield break;
    }

    public Task<string> CompleteAsync(AiPrompt prompt, CancellationToken ct) => Task.FromResult(string.Empty);

    public Task<T?> CompleteJsonAsync<T>(AiPrompt prompt, CancellationToken ct) => Task.FromResult<T?>(default);
}
