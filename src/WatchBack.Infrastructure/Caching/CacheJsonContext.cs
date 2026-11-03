using System.Text.Json.Serialization;

using WatchBack.Core.Models;

namespace WatchBack.Infrastructure.Caching;

[JsonSerializable(typeof(ThoughtResult))]
[JsonSerializable(typeof(List<MediaRating>))]
internal sealed partial class CacheJsonContext : JsonSerializerContext;
