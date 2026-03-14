using System.Text.Json.Serialization;
using WatchBack.Api.Models;

namespace WatchBack.Api.Serialization;

[JsonSerializable(typeof(SyncResponse))]
[JsonSerializable(typeof(ThoughtResponse))]
[JsonSerializable(typeof(ThoughtImageResponse))]
[JsonSerializable(typeof(MediaContextResponse))]
[JsonSerializable(typeof(SourceResultResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class WatchBackJsonContext : JsonSerializerContext { }
