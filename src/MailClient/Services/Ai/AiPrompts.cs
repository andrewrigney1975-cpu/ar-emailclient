namespace MailClient.Services.Ai;

/// Prompt templates. Small local models are prompt-sensitive - keep these short, explicit and
/// low-temperature-friendly. Bodies passed in should already be plain text and truncated.
public static class AiPrompts
{
    public const int MaxBodyChars = 6000;

    public static string Clip(string? text) =>
        (text ?? string.Empty).Length <= MaxBodyChars
            ? text ?? string.Empty
            : text![..MaxBodyChars] + "\n[...truncated]";

    public static AiPrompt Summarise(string subject, string from, string? body) => new()
    {
        System = "You summarise emails. Be concise and factual. Never invent details.",
        User =
            $"Summarise this email in 2-4 short bullet points, then a single line starting " +
            $"\"Action: \" with any action the recipient must take (or \"Action: none\").\n\n" +
            $"From: {from}\nSubject: {subject}\n\n{Clip(body)}",
        MaxTokens = 300,
        Temperature = 0.2f,
    };
}
