using System.Text.Json;
using System.Text.RegularExpressions;
using KodiMCPSharp.Configuration;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace KodiMCPSharp.Services;

public sealed record LearnedRouteEntry(
    string InstanceAlias,
    string AddonId,
    string? AddonName,
    string Name,
    string Target,
    string Media,
    string Kind,
    HandleAction Actions,
    DateTimeOffset SavedUtc,
    LearnedRouteParameter? Parameter = null);

public sealed record LearnedRouteParameter(string Name, string Type, int MaximumLength);

public interface ILearnedRouteStore
{
    Task<LearnedRouteEntry> SaveAsync(LearnedRouteEntry route, CancellationToken cancellationToken);
    Task<IReadOnlyList<LearnedRouteEntry>> ListAsync(string instanceAlias, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(string instanceAlias, string addonId, string name, CancellationToken cancellationToken);
}

public sealed partial class FileLearnedRouteStore : ILearnedRouteStore, IDisposable
{
    internal const string InputPlaceholder = "__KODIMCPSHARP_ROUTE_INPUT__";
    private const int SchemaVersion = 1;
    private const long MaximumFileBytes = 1024 * 1024;
    private const int MaximumAddonFilesPerInstance = 1000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private static readonly HashSet<string> ApprovedTextInputKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "q", "query", "search", "search_query", "searchterm", "search_term", "searchtext", "search_text",
        "keyword", "keywords", "term", "title",
    };

    private readonly string _rootDirectory;
    private readonly int _maximumRoutesPerAddon;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public FileLearnedRouteStore(IOptions<KodiOptions> options)
        : this(ResolveDirectory(options.Value.LearnedRoutes.Directory), options.Value.LearnedRoutes.MaximumRoutesPerAddon)
    {
    }

    internal FileLearnedRouteStore(string rootDirectory, int maximumRoutesPerAddon)
    {
        _rootDirectory = Path.GetFullPath(rootDirectory);
        _maximumRoutesPerAddon = maximumRoutesPerAddon;
    }

    public async Task<LearnedRouteEntry> SaveAsync(LearnedRouteEntry route, CancellationToken cancellationToken)
    {
        ValidateRoute(route);
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var document = await ReadDocumentAsync(route.InstanceAlias, route.AddonId, cancellationToken) ??
                new LearnedRouteDocument(SchemaVersion, route.AddonId, route.AddonName, []);
            var routes = document.Routes.ToList();
            var existingIndex = routes.FindIndex(value => value.Name.Equals(route.Name, StringComparison.OrdinalIgnoreCase));
            var persisted = new PersistedLearnedRoute(
                route.Name, route.Target, route.Media, route.Kind, route.Actions, route.SavedUtc, route.Parameter);
            if (existingIndex >= 0)
            {
                routes[existingIndex] = persisted;
            }
            else
            {
                if (routes.Count >= _maximumRoutesPerAddon)
                {
                    throw new McpException("The learned-route limit for this add-on has been reached.");
                }
                routes.Add(persisted);
            }

            var updated = new LearnedRouteDocument(
                SchemaVersion,
                route.AddonId,
                route.AddonName ?? document.AddonName,
                routes.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase).ToArray());
            await WriteDocumentAsync(route.InstanceAlias, updated, cancellationToken);
            return route with { AddonName = updated.AddonName };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new McpException("The learned-route store could not be written.");
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<IReadOnlyList<LearnedRouteEntry>> ListAsync(string instanceAlias, CancellationToken cancellationToken)
    {
        ValidateSegment(instanceAlias, "Kodi instance alias");
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var directory = GetInstanceDirectory(instanceAlias);
            if (!System.IO.Directory.Exists(directory)) return [];
            var files = System.IO.Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                .Take(MaximumAddonFilesPerInstance + 1)
                .ToArray();
            if (files.Length > MaximumAddonFilesPerInstance)
            {
                throw new McpException("The learned-route store contains too many add-on documents.");
            }

            var results = new List<LearnedRouteEntry>();
            foreach (var file in files)
            {
                var addonId = Path.GetFileNameWithoutExtension(file);
                var document = await ReadDocumentAsync(instanceAlias, addonId, cancellationToken);
                if (document is null) continue;
                results.AddRange(document.Routes.Select(route => new LearnedRouteEntry(
                    instanceAlias,
                    document.AddonId,
                    document.AddonName,
                    route.Name,
                    route.Target,
                    route.Media,
                    route.Kind,
                    route.Actions,
                    route.SavedUtc,
                    route.Parameter)));
            }
            return results
                .OrderBy(value => value.AddonName ?? value.AddonId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new McpException("The learned-route store could not be read.");
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<bool> DeleteAsync(string instanceAlias, string addonId, string name, CancellationToken cancellationToken)
    {
        ValidateSegment(instanceAlias, "Kodi instance alias");
        ValidateAddonId(addonId);
        ValidateName(name);
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var document = await ReadDocumentAsync(instanceAlias, addonId, cancellationToken);
            if (document is null) return false;
            var routes = document.Routes.Where(value => !value.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (routes.Length == document.Routes.Length) return false;
            var path = GetDocumentPath(instanceAlias, addonId);
            if (routes.Length == 0)
            {
                File.Delete(path);
            }
            else
            {
                await WriteDocumentAsync(instanceAlias, document with { Routes = routes }, cancellationToken);
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new McpException("The learned route could not be removed.");
        }
        finally
        {
            _mutex.Release();
        }
    }

    public void Dispose() => _mutex.Dispose();

    internal static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 ||
            name.Any(char.IsControl) || name.IndexOfAny(['/', '\\', '<', '>']) >= 0)
        {
            throw new McpException("A learned route name must be 1-100 characters and cannot contain control, slash, backslash, angle-bracket, or path characters.");
        }
    }

    private async Task<LearnedRouteDocument?> ReadDocumentAsync(
        string instanceAlias,
        string addonId,
        CancellationToken cancellationToken)
    {
        ValidateSegment(instanceAlias, "Kodi instance alias");
        ValidateAddonId(addonId);
        var path = GetDocumentPath(instanceAlias, addonId);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > MaximumFileBytes)
        {
            throw new McpException("A learned-route document exceeds the allowed size.");
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            var document = await JsonSerializer.DeserializeAsync<LearnedRouteDocument>(stream, JsonOptions, cancellationToken);
            if (document is null || document.SchemaVersion != SchemaVersion ||
                !document.AddonId.Equals(addonId, StringComparison.Ordinal) ||
                document.Routes.Length > _maximumRoutesPerAddon)
            {
                throw new McpException("A learned-route document is invalid or uses an unsupported schema.");
            }
            foreach (var route in document.Routes)
            {
                ValidateRoute(new LearnedRouteEntry(instanceAlias, document.AddonId, document.AddonName,
                    route.Name, route.Target, route.Media, route.Kind, route.Actions, route.SavedUtc, route.Parameter));
            }
            return document;
        }
        catch (JsonException)
        {
            throw new McpException("A learned-route document contains invalid JSON.");
        }
    }

    private async Task WriteDocumentAsync(
        string instanceAlias,
        LearnedRouteDocument document,
        CancellationToken cancellationToken)
    {
        var directory = GetInstanceDirectory(instanceAlias);
        CreatePrivateDirectory(_rootDirectory);
        CreatePrivateDirectory(directory);
        var path = GetDocumentPath(instanceAlias, document.AddonId);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            SetPrivateFilePermissions(temporaryPath);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static string ResolveDirectory(string configuredDirectory) =>
        Path.IsPathRooted(configuredDirectory)
            ? configuredDirectory
            : Path.Combine(AppContext.BaseDirectory, configuredDirectory);

    private string GetInstanceDirectory(string instanceAlias) => Path.Combine(_rootDirectory, instanceAlias);

    private string GetDocumentPath(string instanceAlias, string addonId) =>
        Path.Combine(GetInstanceDirectory(instanceAlias), addonId + ".json");

    private static void ValidateRoute(LearnedRouteEntry route)
    {
        ValidateSegment(route.InstanceAlias, "Kodi instance alias");
        ValidateAddonId(route.AddonId);
        ValidateName(route.Name);
        if (route.AddonName is { Length: > 200 } || route.AddonName?.Any(char.IsControl) == true)
        {
            throw new McpException("The learned route contains an invalid add-on name.");
        }
        var targetText = route.Target;
        if (route.Parameter is not null)
        {
            if (route.Parameter is not { Name: "input", Type: "string", MaximumLength: >= 1 and <= 200 } ||
                CountOccurrences(targetText, InputPlaceholder) != 1 ||
                !HasApprovedInputPlaceholder(targetText))
            {
                throw new McpException("The learned route contains an invalid input parameter contract.");
            }
            targetText = targetText.Replace(InputPlaceholder, "synthetic", StringComparison.Ordinal);
        }
        else if (targetText.Contains(InputPlaceholder, StringComparison.Ordinal))
        {
            throw new McpException("The learned route contains an unexpected input placeholder.");
        }
        if (!Uri.TryCreate(targetText, UriKind.Absolute, out var target) ||
            !target.Scheme.Equals("plugin", StringComparison.OrdinalIgnoreCase) ||
            !target.Host.Equals(route.AddonId, StringComparison.OrdinalIgnoreCase))
        {
            throw new McpException("Only a server-observed target belonging to the same Kodi add-on can be learned.");
        }
        var allowedActions = route.Actions & (HandleAction.Browse | HandleAction.Play);
        if (allowedActions == HandleAction.None || route.Actions != allowedActions)
        {
            throw new McpException("The learned route does not contain an allowed browse or play action.");
        }
        if (string.IsNullOrWhiteSpace(route.Media) || route.Media.Length > 30 ||
            string.IsNullOrWhiteSpace(route.Kind) || route.Kind.Length > 80)
        {
            throw new McpException("The learned route contains invalid media or item metadata.");
        }
    }

    private static void ValidateAddonId(string addonId)
    {
        if (!AddonIdPattern().IsMatch(addonId)) throw new McpException("The learned route contains an invalid add-on identity.");
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }
        return count;
    }

    internal static bool IsApprovedTextInputKey(string key) => ApprovedTextInputKeys.Contains(key);

    private static bool HasApprovedInputPlaceholder(string target)
    {
        var queryStart = target.IndexOf('?');
        var queryEnd = queryStart >= 0 ? target.IndexOf('#', queryStart + 1) : -1;
        if (queryStart < 0) return false;
        if (queryEnd < 0) queryEnd = target.Length;
        var segmentStart = queryStart + 1;
        while (segmentStart <= queryEnd)
        {
            var separator = target.IndexOf('&', segmentStart, queryEnd - segmentStart);
            var segmentEnd = separator >= 0 ? separator : queryEnd;
            var equals = target.IndexOf('=', segmentStart, segmentEnd - segmentStart);
            if (equals >= 0 && target[(equals + 1)..segmentEnd].Equals(InputPlaceholder, StringComparison.Ordinal))
            {
                try
                {
                    var key = Uri.UnescapeDataString(target[segmentStart..equals].Replace("+", " ", StringComparison.Ordinal));
                    return IsApprovedTextInputKey(key);
                }
                catch (UriFormatException)
                {
                    return false;
                }
            }
            if (separator < 0) break;
            segmentStart = separator + 1;
        }
        return false;
    }

    private static void ValidateSegment(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new McpException($"{description} is invalid.");
        }
    }

    private static void CreatePrivateDirectory(string path)
    {
        System.IO.Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void SetPrivateFilePermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex AddonIdPattern();

    private sealed record LearnedRouteDocument(
        int SchemaVersion,
        string AddonId,
        string? AddonName,
        PersistedLearnedRoute[] Routes);

    private sealed record PersistedLearnedRoute(
        string Name,
        string Target,
        string Media,
        string Kind,
        HandleAction Actions,
        DateTimeOffset SavedUtc,
        LearnedRouteParameter? Parameter = null);
}
