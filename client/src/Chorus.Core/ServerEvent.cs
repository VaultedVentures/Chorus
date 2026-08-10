using System.Text.Json;

namespace Chorus.Core;

/// <summary>Agent roster entry from hello_ack.</summary>
public sealed record AgentInfo(string Id, string DisplayName, string Voice);

/// <summary>
/// Server→client events (text frames only — never OPUS-decoded, never spoken).
/// </summary>
public abstract record ServerEvent(string Type)
{
    public sealed record HelloAck(string SessionId, string Proto, string Mode,
        IReadOnlyList<AgentInfo> AgentRoster) : ServerEvent("hello_ack");

    public sealed record Turn(string State, string? Complete, int? TimeoutMs) : ServerEvent("turn");

    public sealed record Final(string Text) : ServerEvent("final");

    public sealed record AgentText(string Agent, string Text) : ServerEvent("agent_text");

    /// <summary>Control marker: the NEXT binary frame belongs to this seq.
    /// seq arrives as a JSON number on the wire (gateway sends an int).</summary>
    public sealed record Audio(int Seq, string? Agent) : ServerEvent("audio");

    public sealed record Error(string Code, string? Detail) : ServerEvent("error");

    public sealed record Pong() : ServerEvent("pong");

    public sealed record ByeAck(string SessionId) : ServerEvent("bye_ack");

    public sealed record Unknown(string RawType, string RawJson) : ServerEvent("unknown");

    /// <summary>
    /// Parse a JSON text frame into a typed event. This is the ONLY place
    /// text frames are interpreted; callers must never pass the result to an
    /// Opus decoder or to a speech synthesizer.
    /// </summary>
    public static ServerEvent Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        return type switch
        {
            "hello_ack" => new HelloAck(
                Get(root, "session_id", ""),
                Get(root, "proto", ""),
                Get(root, "mode", "converse"),
                ParseRoster(root)),
            "turn" => new Turn(
                Get(root, "state", ""),
                GetNullable(root, "complete"),
                GetNullableInt(root, "timeout_ms")),
            "final" => new Final(Get(root, "text", "")),
            "agent_text" => new AgentText(Get(root, "agent", ""), Get(root, "text", "")),
            "audio" => new Audio(GetInt(root, "seq", 0), GetNullable(root, "agent")),
            "error" => new Error(Get(root, "code", ""), GetNullable(root, "detail")),
            "pong" => new Pong(),
            "bye_ack" => new ByeAck(Get(root, "session_id", "")),
            _ => new Unknown(type ?? "", json),
        };
    }

    private static IReadOnlyList<AgentInfo> ParseRoster(JsonElement root)
    {
        var list = new List<AgentInfo>();
        if (!root.TryGetProperty("agent_roster", out var roster) || roster.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var a in roster.EnumerateArray())
        {
            list.Add(new AgentInfo(
                Get(a, "id", ""),
                Get(a, "display_name", Get(a, "id", "")),
                Get(a, "voice", "")));
        }
        return list;
    }

    private static string Get(JsonElement e, string name, string fallback) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : fallback;

    private static string? GetNullable(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int GetInt(JsonElement e, string name, int fallback) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : fallback;

    private static int? GetNullableInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : null;
}
