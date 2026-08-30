using KodiMCPSharp.Security;

namespace KodiMCPSharp.Tests;

public sealed class SafeTextTests
{
    private readonly SafeText _safeText = new();

    [Theory]
    [InlineData("plugin://example.addon/?token=secret")]
    [InlineData("https://user:pass@example.invalid/media")]
    [InlineData("C:\\private\\movie.mkv")]
    [InlineData("/private/movie.mkv")]
    public void Clean_RedactsPathsAndUris(string value) => Assert.Equal("[redacted]", _safeText.Clean(value));

    [Fact]
    public void Clean_RemovesControlsAndBoundsLength()
    {
        var result = _safeText.Clean("safe\r\nlabel" + new string('x', 20), 10);

        Assert.Equal("safelabelx", result);
    }
}
