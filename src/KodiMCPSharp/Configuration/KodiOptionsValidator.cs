using Microsoft.Extensions.Options;

namespace KodiMCPSharp.Configuration;

public sealed class KodiOptionsValidator : IValidateOptions<KodiOptions>
{
    public ValidateOptionsResult Validate(string? name, KodiOptions options)
    {
        var failures = new List<string>();
        if (options.RequestTimeoutSeconds is < 1 or > 120) failures.Add("Kodi:RequestTimeoutSeconds must be between 1 and 120.");
        if (options.MaximumResponseBytes is < 65536 or > 16777216) failures.Add("Kodi:MaximumResponseBytes must be between 65536 and 16777216.");
        if (options.MaximumPageSize is < 1 or > 200) failures.Add("Kodi:MaximumPageSize must be between 1 and 200.");
        if (options.Handles.LifetimeMinutes is < 1 or > 1440) failures.Add("Kodi:Handles:LifetimeMinutes must be between 1 and 1440.");
        if (options.Handles.Capacity is < 10 or > 100000) failures.Add("Kodi:Handles:Capacity must be between 10 and 100000.");
        if (string.IsNullOrWhiteSpace(options.LearnedRoutes.Directory) || options.LearnedRoutes.Directory.Length > 1024 ||
            options.LearnedRoutes.Directory.Contains('\0'))
        {
            failures.Add("Kodi:LearnedRoutes:Directory must be a non-empty filesystem directory path of at most 1024 characters.");
        }
        if (options.LearnedRoutes.MaximumRoutesPerAddon is < 1 or > 1000)
        {
            failures.Add("Kodi:LearnedRoutes:MaximumRoutesPerAddon must be between 1 and 1000.");
        }
        if (options.PlaybackNotifications.Capacity is < 10 or > 10000)
            failures.Add("Kodi:PlaybackNotifications:Capacity must be between 10 and 10000.");
        if (options.PlaybackNotifications.ReconnectDelaySeconds is < 1 or > 300)
            failures.Add("Kodi:PlaybackNotifications:ReconnectDelaySeconds must be between 1 and 300.");

        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in options.Instances)
        {
            if (string.IsNullOrWhiteSpace(instance.Alias) || instance.Alias.Length > 64 ||
                instance.Alias.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            {
                failures.Add("Each Kodi instance alias must contain only ASCII letters, digits, '-' or '_' and be at most 64 characters.");
            }
            else if (!aliases.Add(instance.Alias))
            {
                failures.Add($"Kodi instance alias '{instance.Alias}' is duplicated.");
            }

            if (!Uri.TryCreate(instance.Endpoint, UriKind.Absolute, out var endpoint) ||
                endpoint.Scheme is not ("http" or "https") ||
                endpoint.UserInfo.Length > 0 ||
                endpoint.Query.Length > 0 ||
                endpoint.Fragment.Length > 0 ||
                !endpoint.AbsolutePath.TrimEnd('/').EndsWith("/jsonrpc", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"Kodi instance '{instance.Alias}' must use an absolute HTTP(S) endpoint ending in /jsonrpc without embedded credentials, query, or fragment.");
            }
            if (!string.IsNullOrEmpty(instance.WebSocketEndpoint) &&
                (!Uri.TryCreate(instance.WebSocketEndpoint, UriKind.Absolute, out var webSocketEndpoint) ||
                 webSocketEndpoint.Scheme is not ("ws" or "wss") ||
                 webSocketEndpoint.UserInfo.Length > 0 ||
                 webSocketEndpoint.Query.Length > 0 ||
                 webSocketEndpoint.Fragment.Length > 0 ||
                 !webSocketEndpoint.AbsolutePath.TrimEnd('/').EndsWith("/jsonrpc", StringComparison.OrdinalIgnoreCase)))
            {
                failures.Add($"Kodi instance '{instance.Alias}' WebSocket endpoint must use absolute WS(S), end in /jsonrpc, and contain no embedded credentials, query, or fragment.");
            }
        }

        if (options.PlaybackNotifications.Enabled && !options.Instances.Any(instance => !string.IsNullOrEmpty(instance.WebSocketEndpoint)))
            failures.Add("Kodi playback notifications require at least one configured instance WebSocketEndpoint.");

        if (options.DefaultAlias.Length > 0 && !aliases.Contains(options.DefaultAlias))
        {
            failures.Add("Kodi:DefaultAlias must name a configured instance.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
