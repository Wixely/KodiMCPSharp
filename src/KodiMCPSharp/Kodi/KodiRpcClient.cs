using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using KodiMCPSharp.Configuration;
using KodiMCPSharp.Security;

namespace KodiMCPSharp.Kodi;

public sealed class KodiRpcClient : IKodiRpcClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly int _maximumResponseBytes;
    private readonly TimeSpan _timeout;
    private readonly SafeText _safeText;
    private long _requestId;

    public KodiRpcClient(
        HttpClient http,
        KodiInstanceOptions instance,
        KodiOptions options,
        SafeText safeText)
    {
        _http = http;
        _endpoint = new Uri(instance.Endpoint, UriKind.Absolute);
        _maximumResponseBytes = options.MaximumResponseBytes;
        _timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
        _safeText = safeText;
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("KodiMCPSharp/0.1");
        if (!string.IsNullOrEmpty(instance.Username) || !string.IsNullOrEmpty(instance.Password))
        {
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{instance.Username}:{instance.Password}"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", encoded);
        }
    }

    public async Task<JsonElement> CallAsync(
        string method,
        Action<Utf8JsonWriter>? writeParameters = null,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        var requestId = Interlocked.Increment(ref _requestId);
        var payload = WriteRequest(method, requestId, writeParameters);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new ByteArrayContent(payload),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new KodiRpcException(KodiFailureKind.Authentication, "Kodi rejected the configured credentials.");
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new KodiRpcException(KodiFailureKind.Unavailable, $"Kodi returned HTTP status {(int)response.StatusCode}.");
            }
            if (response.Content.Headers.ContentLength > _maximumResponseBytes)
            {
                throw new KodiRpcException(KodiFailureKind.ResponseTooLarge, "Kodi response exceeded the configured size limit.");
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var bytes = await ReadBoundedAsync(responseStream, _maximumResponseBytes, timeout.Token);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var responseId) || responseId.ValueKind != JsonValueKind.Number || responseId.GetInt64() != requestId)
            {
                throw new KodiRpcException(KodiFailureKind.Protocol, "Kodi returned a mismatched JSON-RPC response identifier.");
            }
            if (root.TryGetProperty("error", out var error))
            {
                int? code = error.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var parsedCode) ? parsedCode : null;
                var message = error.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : null;
                throw new KodiRpcException(KodiFailureKind.Remote, _safeText.Error(message), code);
            }
            if (!root.TryGetProperty("result", out var result))
            {
                throw new KodiRpcException(KodiFailureKind.Protocol, "Kodi response did not contain a result.");
            }
            return result.Clone();
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new KodiRpcException(KodiFailureKind.Timeout, "Kodi request timed out.", innerException: exception);
        }
        catch (HttpRequestException exception)
        {
            throw new KodiRpcException(KodiFailureKind.Unavailable, "Kodi could not be reached.", innerException: exception);
        }
        catch (JsonException exception)
        {
            throw new KodiRpcException(KodiFailureKind.Protocol, "Kodi returned malformed JSON.", innerException: exception);
        }
    }

    private static byte[] WriteRequest(string method, long requestId, Action<Utf8JsonWriter>? writeParameters)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("jsonrpc", "2.0");
        writer.WriteString("method", method);
        if (writeParameters is not null)
        {
            writer.WritePropertyName("params");
            writer.WriteStartObject();
            writeParameters(writer);
            writer.WriteEndObject();
        }
        writer.WriteNumber("id", requestId);
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream(Math.Min(limit, 65536));
        var buffer = ArrayPool<byte>.Shared.Rent(16384);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0) break;
                if (memory.Length + read > limit)
                {
                    throw new KodiRpcException(KodiFailureKind.ResponseTooLarge, "Kodi response exceeded the configured size limit.");
                }
                memory.Write(buffer, 0, read);
            }
            return memory.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose() => _http.Dispose();
}
