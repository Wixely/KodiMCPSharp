using KodiMCPSharp.Configuration;

namespace KodiMCPSharp.Tests;

public sealed class ConfigurationTests
{
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
