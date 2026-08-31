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
            "First write 2 to 4 bullet points covering what the email is about.\n" +
            "Then a line `Action: <one thing the reader must do>` (or `Action: none`).\n" +
            "Then a final line. If the email has a payment, bill, deadline, renewal or appointment " +
            "with a date, write exactly:\n" +
            "EVENT: {\"date\": \"YYYY-MM-DD\", \"title\": \"short imperative title\", \"amount\": \"$0.00 or empty\"}\n" +
            "Otherwise write: EVENT: none\n\n" +
            "----- EMAIL -----\n" +
            $"From: {from}\nSubject: {subject}\n\n{Clip(body)}\n" +
            "----- END -----\n\n" +
            "Summary:",
        MaxTokens = 400,
        Temperature = 0.3f,
    };

    public static AiPrompt SuggestReplies(string subject, string from, string? body) => new()
    {
        System = "You draft brief, polite professional email replies. Each reply is 1 to 3 sentences. " +
                 "No greeting, no sign-off, no name. Only use facts from the email.",
        User =
            "Write THREE different short replies to the email below - e.g. one agreeing, one asking a " +
            "question, one declining or deferring. Output exactly three lines, each starting \"1. \", " +
            "\"2. \", \"3. \". Nothing else.\n\n" +
            "----- EMAIL -----\n" +
            $"From: {from}\nSubject: {subject}\n\n{Clip(body)}\n" +
            "----- END -----\n\n" +
            "1.",
        MaxTokens = 320,
        Temperature = 0.6f,
    };

    public static AiPrompt ComposeNew(string instruction) => new()
    {
        System = "You write emails from a short instruction. " +
                 "Output a first line `Subject: <subject>`, then a blank line, then the email body " +
                 "(1-3 short paragraphs). A brief greeting is fine. Do not add a sign-off or a name.",
        User = $"Instruction: {instruction}",
        MaxTokens = 400,
        Temperature = 0.5f,
    };

    public static AiPrompt TodayBrief(string items) => new()
    {
        System = "You write a short daily brief for a busy person. 4 to 8 lines, grouped into tiny " +
                 "sections. Be concise. Only use the items provided - never invent anything.",
        User = $"Write today's brief from:\n\n{items}",
        MaxTokens = 400,
        Temperature = 0.3f,
    };

    public static AiPrompt WeeklyDigest(string items) => new()
    {
        System = "You write a weekly email digest. Group into sections such as 'Bills & payments', " +
                 "'To follow up', and 'FYI'. Short bullet points. Only use the emails provided.",
        User = $"Write the weekly digest from these emails:\n\n{items}",
        MaxTokens = 550,
        Temperature = 0.3f,
    };

    public static AiPrompt ComposeReply(string instruction, string subject, string from, string? body) => new()
    {
        System = "You write email replies from a short instruction. 1 to 4 sentences. " +
                 "No greeting, no sign-off, no name. Only use facts from the email and the instruction.",
        User =
            $"Instruction: {instruction}\n\n" +
            "----- EMAIL -----\n" +
            $"From: {from}\nSubject: {subject}\n\n{Clip(body)}\n" +
            "----- END -----\n\n" +
            "Reply:",
        MaxTokens = 350,
        Temperature = 0.5f,
    };
}
