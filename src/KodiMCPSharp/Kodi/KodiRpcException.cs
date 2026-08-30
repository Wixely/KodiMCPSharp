namespace KodiMCPSharp.Kodi;

public enum KodiFailureKind
{
    Unavailable,
    Authentication,
    Timeout,
    Protocol,
    Remote,
    ResponseTooLarge,
}

public sealed class KodiRpcException(KodiFailureKind kind, string message, int? code = null, Exception? innerException = null)
    : Exception(message, innerException)
{
    public KodiFailureKind Kind { get; } = kind;
    public int? Code { get; } = code;
}
