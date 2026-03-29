using System.Collections.Concurrent;

using WatchBack.Core.Models;

namespace WatchBack.Infrastructure.Tests;

internal sealed class CapturingProgress(ConcurrentBag<SyncProgressTick> bag)
    : IProgress<SyncProgressTick>
{
    public void Report(SyncProgressTick value) => bag.Add(value);
}
