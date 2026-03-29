using Microsoft.Extensions.Options;

namespace WatchBack.Core.Tests;

internal sealed class OptionsSnapshotStub<T>(T value) : IOptionsSnapshot<T>
    where T : class
{
    public T Value => value;
    public T Get(string? name) => value;
}
