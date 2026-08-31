using System.Text.RegularExpressions;

namespace MailClient.Services.Ai;

public static class AiComposeParser
{
    private static readonly Regex SubjectLine =
        new(@"^\s*Subject:\s*(?<s>.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// Splits a "Subject: … \n\n body" draft. Subject is null when the model didn't emit one.
    public static (string? Subject, string Body) ParseNew(string output)
    {
        output = output.Trim();
        var match = SubjectLine.Match(output);
        if (!match.Success || match.Index > 4)
        {
            return (null, output);
        }

        var subject = match.Groups["s"].Value.Trim();
        var body = output[(match.Index + match.Length)..].TrimStart('\r', '\n', ' ');
        return (subject.Length > 0 ? subject : null, body);
    }

    /// Plain-text draft -> the composer's contenteditable HTML (paragraphs / line breaks).
    public static string ToHtml(string text)
    {
        var escaped = System.Net.WebUtility.HtmlEncode(text.Trim()).Replace("\r\n", "\n");
        var paragraphs = Regex.Split(escaped, @"\n\s*\n")
            .Where(p => p.Trim().Length > 0)
            .Select(p => "<p>" + p.Trim().Replace("\n", "<br>") + "</p>");
        var html = string.Concat(paragraphs);
        return html.Length > 0 ? html + "<p><br></p>" : "<p><br></p>";
    }
}
