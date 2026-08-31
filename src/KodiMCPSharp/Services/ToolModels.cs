namespace KodiMCPSharp.Services;

public sealed record InstanceSummary(string Alias, string Availability, string? JsonRpcVersion, string? FailureKind);

public sealed record CapabilitySummary(
    bool ReadOnly,
    string Transport,
    string[] Instances,
    string[] ReadTools,
    string[] ControlTools,
    IReadOnlyDictionary<string, bool> ControlGates,
    HandlePolicySummary Handles,
    LearnedRoutePolicySummary LearnedRoutes);

public sealed record HandlePolicySummary(int LifetimeMinutes, int Capacity, bool SurvivesRestart);

public sealed record LearnedRoutePolicySummary(bool Persistent, bool WriteAllowed, int MaximumRoutesPerAddon);

public sealed record KodiStatusSummary(
    string Alias,
    int? Volume,
    bool? Muted,
    string? ApplicationName,
    string? ApplicationVersion,
    IReadOnlyList<PlayerSummary> Players);

public sealed record PlayerSummary(
    int PlayerId,
    string Type,
    string? State,
    double? Speed,
    double? Percentage,
    TimeSummary? Time,
    TimeSummary? TotalTime,
    int? PlaylistId,
    int? PlaylistPosition,
    string? Repeat,
    bool? Shuffled,
    bool? CanSeek,
    int? CurrentAudioStream,
    int? CurrentVideoStream,
    int? CurrentSubtitle,
    bool? SubtitlesEnabled,
    IReadOnlyList<StreamSummary> AudioStreams,
    IReadOnlyList<StreamSummary> VideoStreams,
    IReadOnlyList<StreamSummary> Subtitles,
    MediaItemSummary? Item);

public sealed record StreamSummary(
    int Index,
    string? Name,
    string? Language,
    string? Codec,
    int? Channels,
    bool? IsDefault,
    bool? IsForced,
    bool? IsImpaired);

public sealed record TimeSummary(int Hours, int Minutes, int Seconds, int Milliseconds);

public sealed record EpisodeWatchStateResult(
    string Alias,
    string RequestedState,
    string? ObservedState,
    bool Accepted,
    bool Observed,
    string Completion);

public sealed record BulkEpisodeWatchStateItem(
    string? Label,
    int SeasonNumber,
    int EpisodeNumber,
    string PreviousState,
    string Outcome);

public sealed record BulkEpisodeWatchStateResult(
    string Alias,
    string RequestedState,
    string Range,
    bool Preview,
    bool IncludeSpecials,
    int Matched,
    int WouldChange,
    int AlreadyTarget,
    int SkippedUnnumbered,
    int Updated,
    int Verified,
    int Failed,
    int MaximumChanges,
    bool CapExceeded,
    int Returned,
    IReadOnlyList<BulkEpisodeWatchStateItem> Episodes);

public sealed record FavouriteMutationResult(
    string Alias,
    string RequestedState,
    string? ObservedState,
    bool Accepted,
    bool Observed,
    string Completion,
    int MatchesBefore,
    int MatchesAfter,
    string? Title,
    string FavouriteType);

public sealed record MediaItemSummary(
    string? Label,
    string? MediaType,
    int? Year,
    int? SeasonNumber,
    int? EpisodeNumber,
    int? EpisodeCount,
    int? WatchedEpisodeCount,
    string[] Artists,
    string? Album,
    string[] Genres,
    int? DurationSeconds,
    int? PlayCount,
    string? WatchState,
    double? ResumePositionSeconds,
    double? ResumeTotalSeconds,
    bool HasArtwork,
    bool IsFolder,
    bool IsPlayable,
    string? Handle,
    string[] AvailableActions,
    string? UnsupportedReason);

public sealed record GenreSummary(string? Name);

public sealed record GenrePageSummary(
    string Alias,
    string Domain,
    int Start,
    int End,
    int Total,
    IReadOnlyList<GenreSummary> Genres);

public sealed record PageSummary(
    string Alias,
    int Start,
    int End,
    int Total,
    IReadOnlyList<MediaItemSummary> Items);

public sealed record AddonSummary(
    string? Name,
    string? AddonType,
    string? Version,
    string? Summary,
    bool Enabled,
    bool Browsable,
    string? Handle);

public sealed record AddonPageSummary(
    string Alias,
    int Start,
    int End,
    int Total,
    IReadOnlyList<AddonSummary> Addons);

public sealed record LearnedRouteSummary(
    string? AddonName,
    string Name,
    string Media,
    string Kind,
    bool CanBrowse,
    bool CanPlay,
    bool RequiresInput,
    string? InputName,
    int? InputMaximumLength,
    DateTimeOffset SavedUtc,
    string Handle);

public sealed record LearnedRoutePageSummary(
    string Alias,
    IReadOnlyList<LearnedRouteSummary> Routes);

public sealed record LearnedRouteMutationResult(string Alias, string Name, bool Removed);

public sealed record BoundLearnedRouteSummary(
    string Alias,
    string Name,
    bool CanBrowse,
    bool CanPlay,
    string Handle);

public sealed record PlaybackResult(
    string Alias,
    string Requested,
    bool Accepted,
    bool Observed,
    string Outcome,
    int? PlayerId,
    string? PlayerType,
    string? State);

public sealed record PlayNextEpisodeResult(
    string Alias,
    string Show,
    string Source,
    string SelectionBasis,
    string? EpisodeLabel,
    int? SeasonNumber,
    int? EpisodeNumber,
    bool Accepted,
    bool Observed,
    string Outcome,
    int? PlayerId,
    string? State);

public sealed record ResolvedPlaybackResult(
    string Alias,
    string Requested,
    string MediaType,
    string Source,
    string SelectionBasis,
    string? Label,
    int? Year,
    int? SeasonNumber,
    int? EpisodeNumber,
    bool Accepted,
    bool Observed,
    string Outcome,
    int? PlayerId,
    string? State);

public sealed record MediaControlResult(
    string Alias,
    string Action,
    bool Accepted,
    bool Observed,
    string Outcome,
    int? PlayerId,
    string? State,
    IReadOnlyDictionary<string, object?> Details);
