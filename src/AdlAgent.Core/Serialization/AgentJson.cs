using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdlAgent.Core.Serialization;

/// <summary>
/// How the agent reads and writes JSON -- one settings object, used
/// everywhere.
/// </summary>
/// <remarks>
/// ADL speaks snake_case, and so does the control protocol; keeping one
/// policy means a property is named once, in C#, and its wire spelling
/// follows. Nulls are left out on the way out because the server reads a
/// missing field and an explicit null the same way, and the smaller body is
/// the one that survives a bad link.
/// </remarks>
public static class AgentJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}
