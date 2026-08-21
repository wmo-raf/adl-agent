using System.Text;

namespace AdlAgent.TestSupport;

/// <summary>
/// One multipart form, as the fake ADL received it.
/// </summary>
/// <remarks>
/// Parsed here rather than handed to a library because the fake server is
/// deliberately a socket and nothing else: the point of testing against real
/// HTTP is that the agent's request has to be a request a server can read,
/// and a parser that only accepts what this agent happens to produce would
/// prove nothing. Django's is the one that matters, so this one is written
/// to the same rule -- the boundary from the content type, parts split on
/// CRLF, headers to the blank line, bytes after it.
/// </remarks>
public sealed record MultipartForm
{
    public required IReadOnlyDictionary<string, string> Fields { get; init; }

    /// <summary>The bytes of the part sent with a filename, if there was one.</summary>
    public byte[]? File { get; init; }

    public string? FileName { get; init; }

    public string? Field(string name) => Fields.TryGetValue(name, out var value) ? value : null;

    /// <summary>Read a form, or <c>null</c> when the body is not one.</summary>
    public static MultipartForm? Parse(string? contentType, byte[] body)
    {
        var boundary = BoundaryOf(contentType);

        if (boundary is null || body.Length == 0)
        {
            return null;
        }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        byte[]? file = null;
        string? fileName = null;

        foreach (var part in Split(body, Encoding.ASCII.GetBytes("--" + boundary)))
        {
            var headerEnd = IndexOf(part, "\r\n\r\n"u8.ToArray(), 0);

            if (headerEnd < 0)
            {
                continue;
            }

            var headers = Encoding.UTF8.GetString(part, 0, headerEnd);
            var start = headerEnd + 4;
            var length = part.Length - start;

            // The delimiter that ended this part was preceded by the CRLF
            // that belongs to the boundary, not to the content.
            if (length >= 2 && part[start + length - 2] == (byte)'\r' && part[start + length - 1] == (byte)'\n')
            {
                length -= 2;
            }

            var name = Parameter(headers, "name=");

            if (name is null)
            {
                continue;
            }

            var partFileName = Parameter(headers, "filename=");

            if (partFileName is not null)
            {
                file = part[start..(start + length)];
                fileName = partFileName;
            }
            else
            {
                fields[name] = Encoding.UTF8.GetString(part, start, length);
            }
        }

        return new MultipartForm { Fields = fields, File = file, FileName = fileName };
    }

    private static string? BoundaryOf(string? contentType)
    {
        if (contentType is null ||
            !contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Parameter(contentType, "boundary=");
    }

    /// <summary>The value of <paramref name="key"/> in a header, quoted or not.</summary>
    private static string? Parameter(string header, string key)
    {
        var at = header.IndexOf(key, StringComparison.OrdinalIgnoreCase);

        if (at < 0)
        {
            return null;
        }

        var value = header[(at + key.Length)..].TrimStart();

        if (value.StartsWith('"'))
        {
            var close = value.IndexOf('"', 1);

            return close < 0 ? null : value[1..close];
        }

        var end = value.IndexOfAny([';', '\r', '\n']);

        return (end < 0 ? value : value[..end]).Trim();
    }

    /// <summary>The bodies between the delimiters, without the preamble or the epilogue.</summary>
    private static IEnumerable<byte[]> Split(byte[] body, byte[] delimiter)
    {
        var at = IndexOf(body, delimiter, 0);

        while (at >= 0)
        {
            var start = at + delimiter.Length;

            // "--" after the delimiter closes the form.
            if (start + 2 <= body.Length && body[start] == (byte)'-' && body[start + 1] == (byte)'-')
            {
                yield break;
            }

            var next = IndexOf(body, delimiter, start);

            if (next < 0)
            {
                yield break;
            }

            // Past the CRLF that ends the delimiter line.
            var from = start + 2 <= body.Length ? start + 2 : start;

            yield return body[from..next];

            at = next;
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int from)
    {
        for (var index = from; index + needle.Length <= haystack.Length; index++)
        {
            var found = true;

            for (var offset = 0; offset < needle.Length; offset++)
            {
                if (haystack[index + offset] != needle[offset])
                {
                    found = false;
                    break;
                }
            }

            if (found)
            {
                return index;
            }
        }

        return -1;
    }
}
