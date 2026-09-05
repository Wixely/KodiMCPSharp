using System.Text.Json;
using KodiMCPSharp.Configuration;
using KodiMCPSharp.Kodi;
using KodiMCPSharp.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KodiMCPSharp.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void ServerOptions_AndPackagedConfigUseAssignedMcpSharpPort()
    {
        Assert.Equal(5720, new ServerOptions().Port);

        using var config = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "KodiMCPSharp.json")));
        Assert.Equal(5720, config.RootElement.GetProperty("Server").GetProperty("Port").GetInt32());
    }

    [Fact]
    public void KodiOptions_AcceptsSafeConfiguration()
    {
        var result = new KodiOptionsValidator().Validate(null, new KodiOptions
        {
            DefaultAlias = "living-room",
            Instances = [new() { Alias = "living-room", Endpoint = "https://example.invalid:8080/jsonrpc" }],
        });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void KodiOptions_IgnoresDisabledPlaceholderInstance()
    {
        var options = new KodiOptions
        {
            Instances =
            [
                new()
                {
                    Enabled = false,
                    Alias = "replace this alias",
                    Endpoint = "<replace-endpoint>",
                    WebSocketEndpoint = "<replace-websocket-endpoint>",
                },
            ],
        };

        var result = new KodiOptionsValidator().Validate(null, options);
        using var registry = new KodiInstanceRegistry(
            Options.Create(options), new SafeText(), NullLogger<KodiInstanceRegistry>.Instance);

        Assert.True(result.Succeeded);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void KodiOptions_DisabledPlaceholderDoesNotSatisfyNotificationEndpointRequirement()
    {
        var result = new KodiOptionsValidator().Validate(null, new KodiOptions
        {
            PlaybackNotifications = new PlaybackNotificationOptions { Enabled = true },
            Instances =
            [
                new()
                {
                    Enabled = false,
                    Alias = "placeholder",
                    Endpoint = "http://example.invalid/jsonrpc",
                    WebSocketEndpoint = "ws://example.invalid:9090/jsonrpc",
                },
            ],
        });

        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData("bad alias", "https://example.invalid/jsonrpc")]
    [InlineData("valid", "ftp://example.invalid/jsonrpc")]
    [InlineData("valid", "https://example.invalid/not-rpc")]
    [InlineData("valid", "https://user:synthetic@example.invalid/jsonrpc")]
    [InlineData("valid", "https://example.invalid/jsonrpc?token=synthetic")]
    public void KodiOptions_RejectsUnsafeIdentityOrEndpoint(string alias, string endpoint)
    {
        var result = new KodiOptionsValidator().Validate(null, new KodiOptions
        {
            Instances = [new() { Alias = alias, Endpoint = endpoint }],
        });

        Assert.True(result.Failed);
    }

    [Fact]
    public void KodiOptions_RejectsDuplicateAliasesCaseInsensitively()
    {
        var result = new KodiOptionsValidator().Validate(null, new KodiOptions
        {
            Instances =
            [
                new() { Alias = "Room", Endpoint = "http://one.invalid/jsonrpc" },
                new() { Alias = "room", Endpoint = "http://two.invalid/jsonrpc" },
            ],
        });

        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public void KodiOptions_RejectsUnsafeLearnedRouteLimits(int maximumRoutes)
    {
        var result = new KodiOptionsValidator().Validate(null, new KodiOptions
        {
            LearnedRoutes = new LearnedRouteOptions { MaximumRoutesPerAddon = maximumRoutes },
        });

        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData("http://example.invalid:9090/jsonrpc")]
    [InlineData("ws://user:synthetic@example.invalid:9090/jsonrpc")]
    [InlineData("ws://example.invalid:9090/not-rpc")]
    [InlineData("ws://example.invalid:9090/jsonrpc?token=synthetic")]
    public void KodiOptions_RejectsUnsafeWebSocketEndpoint(string endpoint)
    {
        var result = new KodiOptionsValidator().Validate(null, new KodiOptions
        {
            Instances = [new() { Alias = "room", Endpoint = "http://example.invalid/jsonrpc", WebSocketEndpoint = endpoint }],
        });

        Assert.True(result.Failed);
    }

    [Fact]
    public void KodiOptions_RequiresExplicitWebSocketEndpointWhenNotificationsEnabled()
    {
        var result = new KodiOptionsValidator().Validate(null, new KodiOptions
        {
            PlaybackNotifications = new PlaybackNotificationOptions { Enabled = true },
            Instances = [new() { Alias = "room", Endpoint = "http://example.invalid/jsonrpc" }],
        });

        Assert.True(result.Failed);
    }

    [Fact]
    public void KodiOptions_AcceptsExplicitSafeWebSocketEndpoint()
    {
        var result = new KodiOptionsValidator().Validate(null, new KodiOptions
        {
            PlaybackNotifications = new PlaybackNotificationOptions { Enabled = true },
            Instances = [new()
            {
                Alias = "room",
                Endpoint = "https://example.invalid/jsonrpc",
                WebSocketEndpoint = "wss://example.invalid:9090/jsonrpc",
            }],
        });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ServerOptions_RequiresPasswordBeyondLoopback()
    {
        var result = new ServerOptionsValidator().Validate(null, new ServerOptions { Host = "0.0.0.0" });

        Assert.True(result.Failed);
    }

    [Fact]
    public void ServerOptions_AllowsPasswordProtectedNetworkBinding()
    {
        var result = new ServerOptionsValidator().Validate(null, new ServerOptions { Host = "0.0.0.0", Password = "synthetic-test-value" });

        Assert.True(result.Succeeded);
    }
}
