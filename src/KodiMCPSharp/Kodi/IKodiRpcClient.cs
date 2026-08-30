using System.Text.Json;

namespace KodiMCPSharp.Kodi;

public interface IKodiRpcClient
{
    Task<JsonElement> CallAsync(
        string method,
        Action<Utf8JsonWriter>? writeParameters = null,
        CancellationToken cancellationToken = default);
}
