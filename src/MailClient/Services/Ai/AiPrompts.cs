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
        System = "You are an assistant that summarises emails. Be concise and factual. " +
                 "Only use information from the email. Never invent details.",
        User =
            "Summarise the email below.\n\n" +
            "Reply with 2 to 4 bullet points covering what the email is about, then one final line " +
            "in the form `Action: <one thing the reader must do>` (write `Action: none` if there is nothing to do).\n\n" +
            "----- EMAIL -----\n" +
            $"From: {from}\nSubject: {subject}\n\n{Clip(body)}\n" +
            "----- END -----\n\n" +
            "Summary:",
        MaxTokens = 350,
        Temperature = 0.3f,
    };
}
