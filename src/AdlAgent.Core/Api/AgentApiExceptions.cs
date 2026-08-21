using System.Net;

namespace AdlAgent.Core.Api;

/// <summary>ADL answered, and the answer was no.</summary>
/// <remarks>
/// <see cref="Code"/> is the stable string the plugin's error envelope
/// carries and the agent switches on; <see cref="Detail"/> is the sentence
/// written for whoever is standing at the machine, and is passed through to
/// the tray unchanged rather than rewritten here.
/// </remarks>
public class AdlRequestException : Exception
{
    public AdlRequestException(HttpStatusCode statusCode, string code, string detail)
        : base(detail)
    {
        StatusCode = statusCode;
        Code = code;
        Detail = detail;
    }

    public HttpStatusCode StatusCode { get; }

    public string Code { get; }

    public string Detail { get; }
}

/// <summary>
/// A 401 on an authenticated call: this device's token is no longer good.
/// </summary>
/// <remarks>
/// Its own type because it is the one server answer that changes what the
/// agent *is* rather than what one call did. ADL deliberately does not say
/// whether the token was revoked, rotated, or never existed -- there is only
/// one thing to do about any of them, which is stop and ask for a new
/// pairing code.
/// </remarks>
public sealed class DeviceRevokedException : AdlRequestException
{
    public DeviceRevokedException(string code, string detail)
        : base(HttpStatusCode.Unauthorized, code, detail)
    {
    }
}

/// <summary>
/// ADL could not be reached at all -- no route, refused, timed out, TLS
/// failed.
/// </summary>
/// <remarks>
/// Told apart from every server answer because it is the normal condition
/// this product exists for: these machines sit on links that come and go,
/// and an unreachable ADL means "work from the cache and try again",
/// never "something is wrong with this install".
/// </remarks>
public sealed class AdlUnreachableException : Exception
{
    public AdlUnreachableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
