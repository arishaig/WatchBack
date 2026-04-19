using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;

namespace WatchBack.Infrastructure.Services;

[JsonSerializable(typeof(SubredditMappingFileDto))]
internal sealed partial class SubredditMappingJsonContext : JsonSerializerContext;

internal sealed record SubredditMappingFileDto(
    [property: JsonPropertyName("mappings")] SubredditMappingFileMappingDto[] Mappings);

internal sealed record SubredditMappingFileMappingDto(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("externalIds")] Dictionary<string, string>? ExternalIds,
    [property: JsonPropertyName("subreddits")] string[] Subreddits);

public sealed class SubredditMappingService : ISubredditMappingService, IDisposable
{
    private const string LocalSourceId = "local";
    private const string BuiltInSourceId = "builtin";

    private readonly string _builtInFilePath;
    private readonly string _userMappingsDirectory;
    private readonly ILogger<SubredditMappingService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Ordered: built-in first, imported files alphabetically, local last.
    // Copy-on-write: readers dereference the field (atomic); writers serialize via _lock
    // and swap the whole array. No separate reader lock needed.
    private ImmutableArray<SubredditMappingSource> _sources = [];

    public SubredditMappingService(
        SubredditMappingPaths paths,
        ILogger<SubredditMappingService> logger)
    {
        _builtInFilePath = paths.BuiltInFilePath;
        _userMappingsDirectory = paths.UserMappingsDirectory;
        _logger = logger;

        Directory.CreateDirectory(_userMappingsDirectory);
        LoadAll();
    }

    public IReadOnlyList<string> GetSubreddits(MediaContext mediaContext)
    {
        ImmutableArray<SubredditMappingSource> snapshot = _sources;

        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (SubredditMappingSource source in snapshot)
        {
            foreach (SubredditMappingEntry entry in source.Entries)
            {
                if (Matches(entry, mediaContext))
                {
                    foreach (string sub in entry.Subreddits)
                    {
                        result.Add(sub);
                    }
                }
            }
        }

        return [.. result];
    }

    public IReadOnlyList<SubredditMappingSource> GetSources()
    {
        return _sources;
    }

    public async Task<SubredditMappingSource> ImportAsync(string name, string json,
        CancellationToken ct = default)
    {
        SubredditMappingFileDto file = ParseJson(json);
        string safeId = SanitizeName(name);

        // Ensure uniqueness by appending a counter if the ID already exists.
        await _lock.WaitAsync(ct);
        try
        {
            string candidateId = safeId;
            int counter = 2;
            while (_sources.Any(s => s.Id.Equals(candidateId, StringComparison.OrdinalIgnoreCase) &&
                                     s.Id != BuiltInSourceId))
            {
                candidateId = $"{safeId}-{counter++}";
            }

            safeId = candidateId;
            string filePath = Path.Combine(_userMappingsDirectory, $"{safeId}.json");
            await WriteFileAtomicAsync(filePath, json, ct);

            SubredditMappingSource source = new(safeId, name, false, ToEntries(file.Mappings));
            AddToSources(source);
            return source;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteSourceAsync(string sourceId, CancellationToken ct = default)
    {
        if (sourceId == BuiltInSourceId)
        {
            throw new InvalidOperationException("The built-in source cannot be deleted.");
        }

        await _lock.WaitAsync(ct);
        try
        {
            string filePath = Path.Combine(_userMappingsDirectory, $"{sourceId}.json");
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            _sources = [.. _sources.Where(s => !s.Id.Equals(sourceId, StringComparison.OrdinalIgnoreCase))];
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task AddLocalEntryAsync(SubredditMappingEntry entry, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            List<SubredditMappingEntry> entries = GetLocalEntriesLocked();
            int existing = entries.FindIndex(e =>
                string.Equals(e.Title, entry.Title, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                entries[existing] = MergeEntry(entries[existing], entry);
            }
            else
            {
                entries.Add(entry);
            }

            await PersistLocalAsync(entries, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteLocalEntryAsync(string title, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            List<SubredditMappingEntry> entries = GetLocalEntriesLocked();
            entries.RemoveAll(e => string.Equals(e.Title, title, StringComparison.OrdinalIgnoreCase));
            await PersistLocalAsync(entries, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task PromoteEntryAsync(string sourceId, int entryIndex, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            SubredditMappingSource? source = _sources.FirstOrDefault(
                s => s.Id.Equals(sourceId, StringComparison.OrdinalIgnoreCase));

            if (source is null || entryIndex < 0 || entryIndex >= source.Entries.Count)
            {
                return;
            }

            SubredditMappingEntry entryToPromote = source.Entries[entryIndex];
            List<SubredditMappingEntry> localEntries = GetLocalEntriesLocked();
            int existing = localEntries.FindIndex(e =>
                string.Equals(e.Title, entryToPromote.Title, StringComparison.OrdinalIgnoreCase));

            if (existing >= 0)
            {
                localEntries[existing] = MergeEntry(localEntries[existing], entryToPromote);
            }
            else
            {
                localEntries.Add(entryToPromote);
            }

            await PersistLocalAsync(localEntries, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public string ExportSource(string sourceId)
    {
        SubredditMappingSource? source = _sources.FirstOrDefault(
            s => s.Id.Equals(sourceId, StringComparison.OrdinalIgnoreCase));

        if (source is null)
        {
            throw new KeyNotFoundException($"Source '{sourceId}' not found.");
        }

        SubredditMappingFileDto dto = new(source.Entries.Select(e => new SubredditMappingFileMappingDto(
            e.Title,
            e.ExternalIds?.ToDictionary(k => k.Key, k => k.Value),
            [.. e.Subreddits])).ToArray());

        return JsonSerializer.Serialize(dto, SubredditMappingJsonContext.Default.SubredditMappingFileDto);
    }

    private void LoadAll()
    {
        List<SubredditMappingSource> sources = [];

        if (File.Exists(_builtInFilePath))
        {
            SubredditMappingSource? builtin = TryLoadFile(_builtInFilePath, BuiltInSourceId, "Built-in", isBuiltIn: true);
            if (builtin is not null)
            {
                sources.Add(builtin);
            }
        }

        // User files loaded alphabetically, with local.json last so manual entries win on overlap.
        IEnumerable<string> userFiles = Directory.EnumerateFiles(_userMappingsDirectory, "*.json")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

        List<string> localFiles = [];
        List<string> otherFiles = [];
        foreach (string path in userFiles)
        {
            string id = Path.GetFileNameWithoutExtension(path);
            if (id.Equals(LocalSourceId, StringComparison.OrdinalIgnoreCase))
            {
                localFiles.Add(path);
            }
            else
            {
                otherFiles.Add(path);
            }
        }

        foreach (string path in otherFiles)
        {
            string id = Path.GetFileNameWithoutExtension(path);
            SubredditMappingSource? source = TryLoadFile(path, id, id, isBuiltIn: false);
            if (source is not null)
            {
                sources.Add(source);
            }
        }

        foreach (string path in localFiles)
        {
            SubredditMappingSource? source = TryLoadFile(path, LocalSourceId, "Local", isBuiltIn: false);
            if (source is not null)
            {
                sources.Add(source);
            }
        }

        _sources = [.. sources];
    }

    private SubredditMappingSource? TryLoadFile(string path, string id, string name, bool isBuiltIn)
    {
        try
        {
            string json = File.ReadAllText(path);
            SubredditMappingFileDto dto = ParseJson(json);
            return new SubredditMappingSource(id, name, isBuiltIn, ToEntries(dto.Mappings));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load subreddit mapping file '{Path}' — skipping", path);
            return null;
        }
    }

    private static SubredditMappingFileDto ParseJson(string json)
    {
        SubredditMappingFileDto? dto = JsonSerializer.Deserialize(json,
            SubredditMappingJsonContext.Default.SubredditMappingFileDto);
        if (dto is null || dto.Mappings is null)
        {
            throw new JsonException("Invalid mapping file: missing 'mappings' array.");
        }

        return dto;
    }

    public void Dispose() => _lock.Dispose();

    private static List<SubredditMappingEntry> ToEntries(SubredditMappingFileMappingDto[] dtos)
    {
        return dtos.Where(d => d.Subreddits is { Length: > 0 })
            .Select(d => new SubredditMappingEntry(
                d.Title,
                d.ExternalIds is { Count: > 0 }
                    ? (IReadOnlyDictionary<string, string>)d.ExternalIds
                    : null,
                d.Subreddits))
            .ToList();
    }

    private static bool Matches(SubredditMappingEntry entry, MediaContext context)
    {
        // External ID match takes priority: any key/value pair in common is a match.
        if (entry.ExternalIds is { Count: > 0 } && context.ExternalIds is { Count: > 0 })
        {
            foreach ((string key, string value) in entry.ExternalIds)
            {
                if (context.ExternalIds.TryGetValue(key, out string? contextValue) &&
                    string.Equals(value, contextValue, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        if (!string.IsNullOrEmpty(entry.Title))
        {
            return string.Equals(entry.Title, context.Title, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private List<SubredditMappingEntry> GetLocalEntriesLocked()
    {
        SubredditMappingSource? local = _sources.FirstOrDefault(
            s => s.Id.Equals(LocalSourceId, StringComparison.OrdinalIgnoreCase));
        return local is not null ? [.. local.Entries] : [];
    }

    private async Task PersistLocalAsync(List<SubredditMappingEntry> entries, CancellationToken ct)
    {
        SubredditMappingFileDto dto = new(entries.Select(e => new SubredditMappingFileMappingDto(
            e.Title,
            e.ExternalIds?.ToDictionary(k => k.Key, k => k.Value),
            [.. e.Subreddits])).ToArray());

        string json = JsonSerializer.Serialize(dto,
            SubredditMappingJsonContext.Default.SubredditMappingFileDto);

        string filePath = Path.Combine(_userMappingsDirectory, $"{LocalSourceId}.json");
        await WriteFileAtomicAsync(filePath, json, ct);

        SubredditMappingSource updated = new(LocalSourceId, "Local", false, entries);
        _sources =
        [
            .. _sources.Where(s => !s.Id.Equals(LocalSourceId, StringComparison.OrdinalIgnoreCase)),
            updated
        ];
    }

    private void AddToSources(SubredditMappingSource source)
    {
        // Insert before local, or at end if no local source exists.
        int localIdx = -1;
        for (int i = 0; i < _sources.Length; i++)
        {
            if (_sources[i].Id.Equals(LocalSourceId, StringComparison.OrdinalIgnoreCase))
            {
                localIdx = i;
                break;
            }
        }

        _sources = localIdx >= 0 ? _sources.Insert(localIdx, source) : _sources.Add(source);
    }

    private static SubredditMappingEntry MergeEntry(SubredditMappingEntry existing, SubredditMappingEntry incoming)
    {
        HashSet<string> merged = new(existing.Subreddits, StringComparer.OrdinalIgnoreCase);
        foreach (string sub in incoming.Subreddits)
        {
            merged.Add(sub);
        }

        IReadOnlyDictionary<string, string>? mergedIds = incoming.ExternalIds ?? existing.ExternalIds;

        return new SubredditMappingEntry(existing.Title ?? incoming.Title, mergedIds, [.. merged]);
    }

    private static string SanitizeName(string name)
    {
        string safe = new([.. name.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')]);
        safe = System.Text.RegularExpressions.Regex.Replace(safe, "-{2,}", "-").Trim('-');
        if (string.IsNullOrEmpty(safe))
        {
            safe = "import";
        }

        return safe.Length > 64 ? safe[..64] : safe;
    }

    private static async Task WriteFileAtomicAsync(string filePath, string content, CancellationToken ct)
    {
        string tmp = filePath + ".tmp";
        await File.WriteAllTextAsync(tmp, content, ct);
        File.Move(tmp, filePath, overwrite: true);
    }
}