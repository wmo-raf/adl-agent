namespace AdlAgent.TestSupport;

/// <summary>One call the agent made, as the fake ADL saw it.</summary>
public sealed record RecordedRequest
{
    public required string Method { get; init; }

    /// <summary>The path below the agent API root, e.g. <c>heartbeat/</c>.</summary>
    public required string Path { get; init; }

    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    /// <summary>The body as text, for the JSON endpoints. Empty for an upload.</summary>
    public required string Body { get; init; }

    /// <summary>The form an upload arrived as, or <c>null</c> for every other call.</summary>
    public MultipartForm? Form { get; init; }

    /// <summary>
    /// What the request said its body was, or <c>-1</c> when it said nothing.
    /// </summary>
    /// <remarks>
    /// Recorded because a body with no declared length is the failure this
    /// agent has already shipped once: ADL is a Django application behind
    /// WSGI, and a chunked request body reaches the view as nothing at all.
    /// </remarks>
    public long ContentLength { get; init; } = -1;

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
