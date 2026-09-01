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
    public LearnedRouteOptions LearnedRoutes { get; set; } = new();
    public PvrOptions Pvr { get; set; } = new();
    public PlaybackNotificationOptions PlaybackNotifications { get; set; } = new();
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
    public bool AllowFullscreenVideo { get; set; }
    public bool AllowWatchState { get; set; }
    public bool AllowFavourites { get; set; }
    public bool AllowLibraryScan { get; set; }
    public bool AllowLibraryClean { get; set; }
    public bool AllowPvrPlayback { get; set; }
}

public sealed class PvrOptions
{
    public bool Enabled { get; set; }
}

public sealed class PlaybackNotificationOptions
{
    public bool Enabled { get; set; }
    public int Capacity { get; set; } = 200;
    public int ReconnectDelaySeconds { get; set; } = 5;
}

public sealed class HandleOptions
{
    public int LifetimeMinutes { get; set; } = 15;
    public int Capacity { get; set; } = 5000;
}

public sealed class LearnedRouteOptions
{
    public string Directory { get; set; } = "kodimcpsharp_data/addon-routes";
    public bool AllowWrite { get; set; }
    public int MaximumRoutesPerAddon { get; set; } = 100;
}

public sealed class KodiInstanceOptions
{
    public bool Enabled { get; set; } = true;
    public string Alias { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool AllowInvalidTlsCertificate { get; set; }
    public string WebSocketEndpoint { get; set; } = string.Empty;
}
