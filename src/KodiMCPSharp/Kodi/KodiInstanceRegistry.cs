using KodiMCPSharp.Configuration;
using KodiMCPSharp.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace KodiMCPSharp.Kodi;

public sealed partial class KodiInstanceRegistry : IDisposable
{
    private readonly Dictionary<string, RegisteredKodiInstance> _instances;
    private readonly string _defaultAlias;

    public KodiInstanceRegistry(IOptions<KodiOptions> options, SafeText safeText, ILogger<KodiInstanceRegistry> logger)
    {
        var configured = options.Value;
        _defaultAlias = configured.DefaultAlias;
        _instances = new Dictionary<string, RegisteredKodiInstance>(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in configured.Instances)
        {
            var handler = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(configured.RequestTimeoutSeconds),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = 8,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            };
            if (instance.AllowInvalidTlsCertificate)
            {
#pragma warning disable CA5359 // Explicit per-instance operator opt-in for private Kodi deployments.
                handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
#pragma warning restore CA5359
                LogInvalidTls(logger, instance.Alias);
            }
            var client = new KodiRpcClient(new HttpClient(handler), instance, configured, safeText);
            _instances.Add(instance.Alias, new RegisteredKodiInstance(instance.Alias, client));
        }
    }

    internal KodiInstanceRegistry(IEnumerable<RegisteredKodiInstance> instances, string defaultAlias = "")
    {
        _instances = instances.ToDictionary(instance => instance.Alias, StringComparer.OrdinalIgnoreCase);
        _defaultAlias = defaultAlias;
    }

    public int Count => _instances.Count;
    public IReadOnlyCollection<RegisteredKodiInstance> Instances => _instances.Values;

    public RegisteredKodiInstance Resolve(string? alias)
    {
        var requested = string.IsNullOrWhiteSpace(alias) ? _defaultAlias : alias.Trim();
        if (requested.Length == 0 && _instances.Count == 1) return _instances.Values.Single();
        if (requested.Length == 0) throw new McpException("A Kodi alias is required because no unique default is configured.");
        if (!_instances.TryGetValue(requested, out var instance)) throw new McpException($"Kodi alias '{requested}' is not configured.");
        return instance;
    }

    public void Dispose()
    {
        foreach (var instance in _instances.Values)
        {
            if (instance.Client is IDisposable disposable) disposable.Dispose();
        }
    }

    [LoggerMessage(LogLevel.Warning, "Invalid TLS certificate validation is enabled for Kodi alias {Alias}")]
    private static partial void LogInvalidTls(ILogger logger, string alias);
}

public sealed record RegisteredKodiInstance(string Alias, IKodiRpcClient Client);
