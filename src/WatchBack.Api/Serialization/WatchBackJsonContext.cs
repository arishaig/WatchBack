using System.Text.Json.Serialization;

using WatchBack.Api.Endpoints;
using WatchBack.Api.Logging;
using WatchBack.Api.Models;
using WatchBack.Core.Models;

namespace WatchBack.Api.Serialization;

[JsonSerializable(typeof(SetManualWatchStateRequest))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(SyncResponse))]
[JsonSerializable(typeof(ThoughtResponse))]
[JsonSerializable(typeof(ThoughtImageResponse))]
[JsonSerializable(typeof(MediaContextResponse))]
[JsonSerializable(typeof(SourceResultResponse))]
[JsonSerializable(typeof(LogEntry))]
[JsonSerializable(typeof(List<LogEntry>))]
[JsonSerializable(typeof(SyncSnapshot))]
[JsonSerializable(typeof(List<ProviderSyncRecord>))]
[JsonSerializable(typeof(DiagnosticsStatusResponse))]
[JsonSerializable(typeof(IReadOnlyList<MediaSearchResult>))]
[JsonSerializable(typeof(IReadOnlyList<SeasonInfo>))]
[JsonSerializable(typeof(IReadOnlyList<EpisodeInfo>))]
[JsonSerializable(typeof(IReadOnlyList<MediaRatingResponse>))]
[JsonSerializable(typeof(MediaRatingResponse))]
[JsonSerializable(typeof(SyncEndpoints.ProgressEvent))]
[JsonSerializable(typeof(SyncEndpoints.ProgressSegment))]
[JsonSerializable(typeof(SyncEndpoints.ProgressSegment[]))]
[JsonSerializable(typeof(ThemeItem[]))]
[JsonSerializable(typeof(List<SyncLogEntry>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class WatchBackJsonContext : JsonSerializerContext;
