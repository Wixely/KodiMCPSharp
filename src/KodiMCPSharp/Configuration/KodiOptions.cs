namespace KodiMCPSharp.Configuration;

public sealed class KodiOptions
{
    public const string SectionName = "Kodi";

    public string DefaultAlias { get; set; } = string.Empty;
    public int RequestTimeoutSeconds { get; set; } = 10;
    public int MaximumResponseBytes { get; set; } = 2 * 1024 * 1024;
    public int MaximumPageSize { get; set; } = 50;
    public bool ReadOnly { get; set; } = true;
    public KodiControlOptions Controls { get; set; } = new();
    public HandleOptions Handles { get; set; } = new();
    public List<KodiInstanceOptions> Instances { get; set; } = [];
}

public sealed class KodiControlOptions
{
    public bool AllowPlayback { get; set; }
    public bool AllowPlayerControl { get; set; }
    public bool AllowSeek { get; set; }
    public bool AllowVolume { get; set; }
    public bool AllowStreamSelection { get; set; }
    public bool AllowPlaybackModes { get; set; }
    public bool AllowPlaylists { get; set; }
}

public sealed class HandleOptions
{
    public int LifetimeMinutes { get; set; } = 15;
    public int Capacity { get; set; } = 5000;
}

public sealed class KodiInstanceOptions
{
    public string Alias { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool AllowInvalidTlsCertificate { get; set; }
}
