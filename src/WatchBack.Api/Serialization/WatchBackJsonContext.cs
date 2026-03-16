using System.Text.Json.Serialization;

using WatchBack.Api.Logging;
using WatchBack.Api.Models;

namespace WatchBack.Api.Serialization;

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
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class WatchBackJsonContext : JsonSerializerContext { }