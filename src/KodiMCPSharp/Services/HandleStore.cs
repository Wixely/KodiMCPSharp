using System.Security.Cryptography;
using KodiMCPSharp.Configuration;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace KodiMCPSharp.Services;

[Flags]
public enum HandleAction
{
    None = 0,
    Browse = 1,
    Play = 2,
    LibraryBrowse = 4,
    ManageLearnedRoute = 8,
    BindLearnedRoute = 16,
}

public sealed record HandleEntry(
    string InstanceAlias,
    string Target,
    string Media,
    string Kind,
    HandleAction Actions,
    DateTimeOffset ExpiresUtc,
    string? AddonId = null,
    string? AddonName = null,
    string? LearnedRouteName = null,
    HandleAction TemplateResultActions = HandleAction.None);

public interface IHandleStore
{
    string Create(
        string instanceAlias,
        string target,
        string media,
        string kind,
        HandleAction actions,
        string? addonId = null,
        string? addonName = null,
        string? learnedRouteName = null,
        HandleAction templateResultActions = HandleAction.None);
    HandleEntry Resolve(string handle, string instanceAlias, HandleAction requiredAction);
    int Count { get; }
}

public sealed class InMemoryHandleStore : IHandleStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, HandleEntry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _lifetime;
    private readonly int _capacity;

    public InMemoryHandleStore(IOptions<KodiOptions> options, TimeProvider timeProvider)
        : this(options.Value.Handles.LifetimeMinutes, options.Value.Handles.Capacity, timeProvider)
    {
    }

    internal InMemoryHandleStore(int lifetimeMinutes, int capacity, TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _lifetime = TimeSpan.FromMinutes(lifetimeMinutes);
        _capacity = capacity;
    }

    public int Count
    {
        get { lock (_sync) return _entries.Count; }
    }

    public string Create(
        string instanceAlias,
        string target,
        string media,
        string kind,
        HandleAction actions,
        string? addonId = null,
        string? addonName = null,
        string? learnedRouteName = null,
        HandleAction templateResultActions = HandleAction.None)
    {
        lock (_sync)
        {
            PruneExpired();
            while (_entries.Count >= _capacity)
            {
                var oldest = _entries.MinBy(pair => pair.Value.ExpiresUtc);
                _entries.Remove(oldest.Key);
            }
            string handle;
            do
            {
                handle = "h_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            } while (_entries.ContainsKey(handle));
            _entries.Add(handle, new HandleEntry(
                instanceAlias, target, media, kind, actions, _timeProvider.GetUtcNow().Add(_lifetime),
                addonId, addonName, learnedRouteName, templateResultActions));
            return handle;
        }
    }

    public HandleEntry Resolve(string handle, string instanceAlias, HandleAction requiredAction)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(handle ?? string.Empty, out var entry) || entry.ExpiresUtc <= _timeProvider.GetUtcNow())
            {
                _entries.Remove(handle ?? string.Empty);
                throw new McpException("The item handle is invalid or expired. Browse again to obtain a fresh handle.");
            }
            if (!entry.InstanceAlias.Equals(instanceAlias, StringComparison.OrdinalIgnoreCase))
            {
                throw new McpException("The item handle belongs to a different Kodi instance.");
            }
            if ((entry.Actions & requiredAction) != requiredAction)
            {
                throw new McpException("The item handle does not permit that action.");
            }
            return entry;
        }
    }

    private void PruneExpired()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var key in _entries.Where(pair => pair.Value.ExpiresUtc <= now).Select(pair => pair.Key).ToArray())
        {
            _entries.Remove(key);
        }
    }
}
