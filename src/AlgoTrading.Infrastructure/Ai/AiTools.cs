using System.Globalization;
using System.Text.Json.Nodes;

namespace AlgoTrading.Infrastructure.Ai;

/// <summary>
/// A read-only view of the desk that a model may ask for while it answers.
/// </summary>
/// <remarks>
/// <para>
/// A tool reads; it never writes, places, cancels or switches anything. That
/// is the whole safety case for letting a model choose when to call one, so
/// an implementation takes only query services and returns a projection.
/// </para>
/// <para>
/// A tool's answer leaves the server (it is sent to the model's provider), so
/// it is built field by field from what the question needs: never an entity,
/// never a DTO passed through whole. No tokens, broker client ids, emails or
/// keys; <c>AiToolDataTests</c> checks every tool's output for them.
/// </para>
/// </remarks>
public interface IAiTool
{
    /// <summary>The name the model calls it by: lower snake case, as providers expect.</summary>
    string Name { get; }

    /// <summary>What it returns and when to use it, written for the model.</summary>
    string Description { get; }

    /// <summary>Its arguments as a JSON schema (an object with properties), empty when it takes none.</summary>
    JsonObject Parameters { get; }

    /// <summary>
    /// How long it may run before it is stopped; null for <see cref="AiSettings.ToolTimeoutSeconds"/>. Longer only
    /// for a tool that itself waits on a model (the AI Trader's look now).
    /// </summary>
    TimeSpan? Timeout => null;

    Task<AiToolOutput> RunAsync(AiToolArgs args, CancellationToken cancellationToken);
}

/// <summary>Who a tool runs for: the call that asked for it, so a tool that asks a model itself asks as the same person.</summary>
public sealed record AiToolCaller(string AgentKey, string Source, string RequestedBy, long? UserId, string ConversationId);

/// <summary>What a tool found.</summary>
/// <param name="Data">Serialised as JSON (camelCase) for the model.</param>
/// <param name="AsOfUtc">When the data was true: the newest row, the quote time. Null when it has no time.</param>
/// <param name="Rows">How many items it holds, for the console's one-line summary.</param>
/// <param name="Summary">One line for the console, e.g. "3 runs, net −₹4,210".</param>
public sealed record AiToolOutput(object Data, DateTime? AsOfUtc, int Rows, string Summary);

/// <summary>A tool's arguments were wrong in a way the model can fix: the message goes back to it.</summary>
public sealed class AiToolArgumentException(string message) : Exception(message);

/// <summary>A tool's arguments as the model wrote them, read with checks that answer in words.</summary>
public sealed class AiToolArgs
{
    private readonly JsonObject _values;

    public AiToolArgs(JsonObject values) => _values = values;

    public static AiToolArgs Empty { get; } = new(new JsonObject());

    /// <summary>The call the tool runs for; null outside a model call (a test, a scheduled agent reading a tool's answer).</summary>
    public AiToolCaller? Caller { get; private init; }

    /// <summary>The same arguments, run for <paramref name="caller"/>.</summary>
    public AiToolArgs For(AiToolCaller caller) => new(_values) { Caller = caller };

    /// <summary>The arguments' JSON as the model wrote it, or an empty object.</summary>
    public static AiToolArgs Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty;
        try
        {
            return JsonNode.Parse(json) is JsonObject obj ? new AiToolArgs(obj) : throw new AiToolArgumentException("The arguments must be a JSON object.");
        }
        catch (System.Text.Json.JsonException)
        {
            throw new AiToolArgumentException("The arguments are not valid JSON.");
        }
    }

    public string? String(string name, int maxLength = 100)
    {
        var node = _values[name];
        if (node is null) return null;
        string? text = node is JsonValue v && v.TryGetValue(out string? s) ? s : node.ToString();
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Length > maxLength) throw new AiToolArgumentException($"{name} is longer than {maxLength} characters.");
        return text;
    }

    public long? Long(string name, long min = long.MinValue, long max = long.MaxValue)
    {
        var node = _values[name];
        if (node is null) return null;
        long value;
        if (node is JsonValue v && v.TryGetValue(out long l)) value = l;
        else if (node is JsonValue d && d.TryGetValue(out double dbl) && dbl == Math.Floor(dbl)) value = (long)dbl;
        else if (long.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)) value = parsed;
        else throw new AiToolArgumentException($"{name} must be a whole number.");

        if (value < min || value > max) throw new AiToolArgumentException($"{name} must be between {min} and {max}.");
        return value;
    }

    public int? Int(string name, int min, int max) => (int?)Long(name, min, max);

    /// <summary>An IST calendar date as yyyy-MM-dd, or "today"/"yesterday".</summary>
    public DateOnly? Date(string name, DateOnly today)
    {
        string? text = String(name, 20);
        if (text is null) return null;
        if (text.Equals("today", StringComparison.OrdinalIgnoreCase)) return today;
        if (text.Equals("yesterday", StringComparison.OrdinalIgnoreCase)) return today.AddDays(-1);
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date;
        throw new AiToolArgumentException($"{name} must be a date as yyyy-MM-dd.");
    }

    public bool? Bool(string name)
    {
        var node = _values[name];
        if (node is null) return null;
        if (node is JsonValue v && v.TryGetValue(out bool b)) return b;
        if (bool.TryParse(node.ToString(), out bool parsed)) return parsed;
        throw new AiToolArgumentException($"{name} must be true or false.");
    }
}

/// <summary>Builds a tool's parameter schema without hand-writing JSON.</summary>
public static class AiToolSchema
{
    public static JsonObject Object(params (string Name, JsonObject Schema, bool Required)[] properties)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, schema, isRequired) in properties)
        {
            props[name] = schema;
            if (isRequired) required.Add(name);
        }

        return new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = required };
    }

    public static JsonObject Text(string description) => new() { ["type"] = "string", ["description"] = description };

    public static JsonObject Integer(string description) => new() { ["type"] = "integer", ["description"] = description };

    public static JsonObject Flag(string description) => new() { ["type"] = "boolean", ["description"] = description };

    public static JsonObject OneOf(string description, params string[] values) =>
        new() { ["type"] = "string", ["description"] = description, ["enum"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray()) };
}

/// <summary>The tools the desk offers its agents, by name; each agent gets the ones its catalog entry lists.</summary>
public sealed class AiToolbox
{
    private readonly Dictionary<string, IAiTool> _tools;

    public AiToolbox(IEnumerable<IAiTool> tools)
    {
        _tools = new Dictionary<string, IAiTool>(StringComparer.Ordinal);
        foreach (var tool in tools) _tools[tool.Name] = tool;
    }

    public IReadOnlyCollection<IAiTool> All => _tools.Values;

    public IAiTool? Find(string name) => _tools.GetValueOrDefault(name);

    /// <summary>The agent's tools that exist on this build, in its catalog order.</summary>
    public IReadOnlyList<IAiTool> For(AiAgentDef agent) => For(agent, chat: false);

    /// <summary>
    /// The agent's tools that exist on this build, in its catalog order: in a chat (<see cref="AiCatalog.ChatsAs"/>)
    /// its chat tools, which read its own work, else the tools its scheduled work uses.
    /// </summary>
    public IReadOnlyList<IAiTool> For(AiAgentDef agent, bool chat) =>
        ((chat ? agent.ChatTools ?? agent.Tools : agent.Tools) ?? []).Select(Find).Where(t => t is not null).Select(t => t!).ToList();
}
