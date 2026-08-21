namespace AdlAgent.TestSupport;

/// <summary>One call the agent made, as the fake ADL saw it.</summary>
public sealed record RecordedRequest
{
    public required string Method { get; init; }

    /// <summary>The path below the agent API root, e.g. <c>heartbeat/</c>.</summary>
    public required string Path { get; init; }

    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    public required string Body { get; init; }

    public string? Header(string name) =>
        Headers.TryGetValue(name, out var value) ? value : null;

    /// <summary>The bearer token this call presented, if any.</summary>
    public string? BearerToken
    {
        get
        {
            var authorization = Header("Authorization");

            return authorization is not null && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? authorization["Bearer ".Length..]
                : null;
        }
    }
}
