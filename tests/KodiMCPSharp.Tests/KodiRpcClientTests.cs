using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using KodiMCPSharp.Configuration;
using KodiMCPSharp.Kodi;
using KodiMCPSharp.Security;

namespace KodiMCPSharp.Tests;

public sealed class KodiRpcClientTests
{
    [Fact]
    public async Task Call_SendsTypedJsonRpcAndBasicAuthentication()
    {
        string? requestJson = null;
        AuthenticationHeaderValue? authorization = null;
        var handler = new DelegateHandler(async request =>
        {
            authorization = request.Headers.Authorization;
            requestJson = await request.Content!.ReadAsStringAsync();
            using var requestDocument = JsonDocument.Parse(requestJson);
            var id = requestDocument.RootElement.GetProperty("id").GetInt64();
            return Json(HttpStatusCode.OK, $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"value\":42}}}}");
        });
        using var client = CreateClient(handler, username: "operator", password: "synthetic");

        var result = await client.CallAsync("JSONRPC.Version", writer => writer.WriteString("sample", "value"), TestContext.Current.CancellationToken);

        Assert.Equal(42, result.GetProperty("value").GetInt32());
        Assert.Equal("Basic", authorization?.Scheme);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("operator:synthetic")), authorization?.Parameter);
        Assert.Contains("\"method\":\"JSONRPC.Version\"", requestJson, StringComparison.Ordinal);
        Assert.Contains("\"params\":{\"sample\":\"value\"}", requestJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Call_ClassifiesAuthenticationFailure()
    {
        using var client = CreateClient(new DelegateHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))));

        var exception = await Assert.ThrowsAsync<KodiRpcException>(() => client.CallAsync("JSONRPC.Version", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(KodiFailureKind.Authentication, exception.Kind);
    }

    [Fact]
    public async Task Call_RejectsMismatchedIdentifier()
    {
        using var client = CreateClient(new DelegateHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, "{\"jsonrpc\":\"2.0\",\"id\":999,\"result\":{}}"))));

        var exception = await Assert.ThrowsAsync<KodiRpcException>(() => client.CallAsync("JSONRPC.Version", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(KodiFailureKind.Protocol, exception.Kind);
    }

    [Fact]
    public async Task Call_RedactsSensitiveRemoteError()
    {
        var handler = new DelegateHandler(async request =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var id = document.RootElement.GetProperty("id").GetInt64();
            return Json(HttpStatusCode.OK, $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"error\":{{\"code\":-1,\"message\":\"plugin://private/?token=synthetic\"}}}}");
        });
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<KodiRpcException>(() => client.CallAsync("Files.GetDirectory", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(KodiFailureKind.Remote, exception.Kind);
        Assert.Equal("[redacted]", exception.Message);
    }

    [Fact]
    public async Task Call_RejectsOversizedResponse()
    {
        var handler = new DelegateHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, new string('x', 200))));
        using var client = CreateClient(handler, maximumBytes: 100);

        var exception = await Assert.ThrowsAsync<KodiRpcException>(() => client.CallAsync("JSONRPC.Version", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(KodiFailureKind.ResponseTooLarge, exception.Kind);
    }

    [Fact]
    public async Task Call_RejectsMalformedJson()
    {
        using var client = CreateClient(new DelegateHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, "not-json"))));

        var exception = await Assert.ThrowsAsync<KodiRpcException>(() => client.CallAsync("JSONRPC.Version", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(KodiFailureKind.Protocol, exception.Kind);
    }

    private static KodiRpcClient CreateClient(HttpMessageHandler handler, string username = "", string password = "", int maximumBytes = 65536) =>
        new(new HttpClient(handler), new KodiInstanceOptions
        {
            Alias = "test",
            Endpoint = "http://example.invalid/jsonrpc",
            Username = username,
            Password = password,
        }, new KodiOptions { MaximumResponseBytes = maximumBytes }, new SafeText());

    private static HttpResponseMessage Json(HttpStatusCode status, string content) => new(status)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json"),
    };

    private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
    }
}
