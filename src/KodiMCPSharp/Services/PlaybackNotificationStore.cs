using System.Text.Json;
using KodiMCPSharp.Configuration;
using KodiMCPSharp.Security;
using Microsoft.Extensions.Options;

namespace KodiMCPSharp.Services;

public sealed class PlaybackNotificationStore
{
    private static readonly HashSet<string> AllowedMethods =
    [
        "Player.OnAVStart", "Player.OnAVChange", "Player.OnPlay", "Player.OnPause", "Player.OnResume",
        "Player.OnSeek", "Player.OnSpeedChanged", "Player.OnStop", "Player.OnPropertyChanged",
    ];

    private readonly object _sync = new();
    private readonly int _capacity;
    private readonly SafeText _safeText;
    private readonly Dictionary<string, InstanceState> _states = new(StringComparer.OrdinalIgnoreCase);
    private long _sequence;

    public PlaybackNotificationStore(IOptions<KodiOptions> options, SafeText safeText)
    {
        _capacity = options.Value.PlaybackNotifications.Capacity;
        _safeText = safeText;
        foreach (var instance in options.Value.Instances)
            _states[instance.Alias] = new InstanceState(!string.IsNullOrEmpty(instance.WebSocketEndpoint));
    }

    public void SetConnected(string alias, DateTimeOffset now)
    {
        lock (_sync)
        {
            var state = GetOrCreate(alias);
            state.Connected = true;
            state.LastConnectedUtc = now;
            state.LastFailureKind = null;
        }
    }

    public void SetDisconnected(string alias, string? failureKind)
    {
        lock (_sync)
        {
            var state = GetOrCreate(alias);
            state.Connected = false;
            state.LastFailureKind = failureKind;
        }
    }

    public bool Record(string alias, JsonElement notification, DateTimeOffset receivedUtc)
    {
        var method = GetString(notification, "method");
        if (method is null || !AllowedMethods.Contains(method) ||
            !notification.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object)
            return false;
        var data = parameters.TryGetProperty("data", out var dataValue) && dataValue.ValueKind == JsonValueKind.Object
            ? dataValue
            : default;
        var item = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("item", out var itemValue) && itemValue.ValueKind == JsonValueKind.Object
            ? itemValue
            : default;
        var player = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("player", out var playerValue) && playerValue.ValueKind == JsonValueKind.Object
            ? playerValue
            : default;
        var time = player.ValueKind == JsonValueKind.Object && player.TryGetProperty("time", out var timeValue) && timeValue.ValueKind == JsonValueKind.Object
            ? new TimeSummary(GetInt(timeValue, "hours") ?? 0, GetInt(timeValue, "minutes") ?? 0,
                GetInt(timeValue, "seconds") ?? 0, GetInt(timeValue, "milliseconds") ?? 0)
            : null;
        var summary = new PlaybackNotificationSummary(
            Interlocked.Increment(ref _sequence),
            receivedUtc,
            NormalizeEvent(method),
            GetInt(player, "playerid"),
            _safeText.Clean(GetString(item, "type"), 30),
            _safeText.Clean(GetString(item, "title") ?? GetString(item, "label")),
            GetInt(item, "season"),
            GetInt(item, "episode"),
            GetInt(player, "speed"),
            data.ValueKind == JsonValueKind.Object ? GetBool(data, "end") : null,
            time);
        lock (_sync)
        {
            var state = GetOrCreate(alias);
            state.Events.Enqueue(summary);
            while (state.Events.Count > _capacity) state.Events.Dequeue();
            state.LastEventUtc = receivedUtc;
        }
        return true;
    }

    public PlaybackNotificationResult Read(string alias, long? afterSequence, int limit)
    {
        lock (_sync)
        {
            var state = GetOrCreate(alias);
            var events = state.Events
                .Where(value => afterSequence is null || value.Sequence > afterSequence.Value)
                .TakeLast(limit)
                .ToArray();
            return new PlaybackNotificationResult(
                alias,
                afterSequence,
                limit,
                events.Length,
                state.Events.Count == 0 ? null : state.Events.Last().Sequence,
                new PlaybackNotificationConnectionSummary(state.Configured, state.Connected, state.LastConnectedUtc,
                    state.LastEventUtc, state.LastFailureKind),
                events);
        }
    }

    private InstanceState GetOrCreate(string alias)
    {
        if (_states.TryGetValue(alias, out var state)) return state;
        state = new InstanceState(false);
        _states[alias] = state;
        return state;
    }

    private static string NormalizeEvent(string method) => method switch
    {
        "Player.OnAVStart" => "av-start",
        "Player.OnAVChange" => "av-change",
        "Player.OnPlay" => "play",
        "Player.OnPause" => "pause",
        "Player.OnResume" => "resume",
        "Player.OnSeek" => "seek",
        "Player.OnSpeedChanged" => "speed-changed",
        "Player.OnStop" => "stop",
        _ => "property-changed",
    };

    private static string? GetString(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var result) && result.ValueKind == JsonValueKind.String
            ? result.GetString()
            : null;

    private static int? GetInt(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var result) && result.TryGetInt32(out var number)
            ? number
            : null;

    private static bool? GetBool(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var result) && result.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? result.GetBoolean()
            : null;

    private sealed class InstanceState(bool configured)
    {
        public bool Configured { get; } = configured;
        public bool Connected { get; set; }
        public DateTimeOffset? LastConnectedUtc { get; set; }
        public DateTimeOffset? LastEventUtc { get; set; }
        public string? LastFailureKind { get; set; }
        public Queue<PlaybackNotificationSummary> Events { get; } = new();
    }
}
