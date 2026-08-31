using System.Text.RegularExpressions;

namespace MailClient.Services.Ai;

/// Splits the model's "1. … 2. … 3. …" reply output into individual drafts.
public static class AiReplyParser
{
    // The prompt ends with "1." so the first reply text may arrive without its own number.
    private static readonly Regex Item = new(@"(?:^|\n)\s*(\d)[.)]\s*", RegexOptions.Compiled);

    public static List<string> Parse(string modelOutput)
    {
        var text = "1. " + modelOutput.Trim();
        var matches = Item.Matches(text);
        var replies = new List<string>();

        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var reply = text[start..end].Trim().Trim('"').Trim();
            if (reply.Length is > 0 and < 1200)
            {
                replies.Add(reply);
            }
        }

        return replies.Take(3).ToList();
    }
}
