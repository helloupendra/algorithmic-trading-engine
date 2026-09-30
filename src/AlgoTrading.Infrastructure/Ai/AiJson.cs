using System.Text.Json;
using System.Text.Json.Nodes;

namespace AlgoTrading.Infrastructure.Ai;

/// <summary>
/// The JSON object in a model's answer, however the model wrapped it: bare,
/// in a ```json fence, or with a sentence before or after.
/// </summary>
/// <remarks>
/// The same leniency as <c>core/llm.py</c>'s <c>parse_json</c>: models on the
/// free tier are asked for JSON and mostly comply, but a fence or a "Here is
/// the JSON:" line is common, and a report should not fail for it. What is
/// inside is still checked by the agent that asked.
/// </remarks>
public static class AiJson
{
    /// <summary>The first JSON object in <paramref name="text"/>, or null when there is none that parses.</summary>
    public static JsonObject? Object(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string body = text.Trim();

        // A fenced block wins: it is where the model meant the JSON to be.
        int fence = body.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            int start = body.IndexOf('\n', fence);
            int end = start < 0 ? -1 : body.IndexOf("```", start, StringComparison.Ordinal);
            if (start >= 0 && end > start && TryParse(body[(start + 1)..end]) is JsonObject fenced) return fenced;
        }

        if (TryParse(body) is JsonObject whole) return whole;

        // Otherwise the first balanced {...}, reading strings so a brace inside one does not count.
        for (int open = body.IndexOf('{'); open >= 0; open = body.IndexOf('{', open + 1))
        {
            int close = MatchingBrace(body, open);
            if (close > open && TryParse(body[open..(close + 1)]) is JsonObject found) return found;
        }

        return null;
    }

    private static JsonNode? TryParse(string text)
    {
        try
        {
            return JsonNode.Parse(text.Trim());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int MatchingBrace(string text, int open)
    {
        int depth = 0;
        bool inString = false;
        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
        }

        return -1;
    }

    /// <summary>A string property, trimmed; null when missing or not a string.</summary>
    public static string? Str(JsonObject obj, string name) =>
        obj[name] is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    /// <summary>A number property; null when missing or not a number.</summary>
    public static double? Num(JsonObject obj, string name) =>
        obj[name] is JsonValue v && v.TryGetValue(out double d) ? d : null;

    /// <summary>A list of strings, skipping anything that is not one.</summary>
    public static List<string> Strings(JsonObject obj, string name) =>
        obj[name] is JsonArray a
            ? a.OfType<JsonValue>().Select(v => v.TryGetValue(out string? s) ? s?.Trim() : null).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList()
            : [];
}
