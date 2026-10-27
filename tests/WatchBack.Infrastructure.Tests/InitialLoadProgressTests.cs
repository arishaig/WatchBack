using FluentAssertions;

using Microsoft.Extensions.Caching.Memory;

using NSubstitute;

using WatchBack.Core.Models;
using WatchBack.Infrastructure.ThoughtProviders;

using Xunit;

namespace WatchBack.Infrastructure.Tests;

public class InitialLoadProgressTests : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public void Dispose()
    {
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ForFetch_FirstCall_ReturnsProgress()
    {
        IProgress<SyncProgressTick> progress = Substitute.For<IProgress<SyncProgressTick>>();

        InitialLoadProgress.ForFetch(_cache, "key", progress).Should().BeSameAs(progress);
    }

    [Fact]
    public void ForFetch_SecondCallForSameKey_ReturnsNull()
    {
        IProgress<SyncProgressTick> progress = Substitute.For<IProgress<SyncProgressTick>>();
        InitialLoadProgress.ForFetch(_cache, "key", progress);

        InitialLoadProgress.ForFetch(_cache, "key", progress).Should().BeNull();
    }

    [Fact]
    public void ForFetch_DifferentKey_ReturnsProgress()
    {
        IProgress<SyncProgressTick> progress = Substitute.For<IProgress<SyncProgressTick>>();
        InitialLoadProgress.ForFetch(_cache, "episode-1", progress);

        InitialLoadProgress.ForFetch(_cache, "episode-2", progress).Should().BeSameAs(progress);
    }
}
