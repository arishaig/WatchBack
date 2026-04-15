using FluentAssertions;

using NSubstitute;

using WatchBack.Api;
using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;

using Xunit;

namespace WatchBack.Api.Tests;

public sealed class SyncProgressReporterTests
{
    private static IThoughtProvider Provider(string name, int weight, string? color = null)
    {
        IThoughtProvider provider = Substitute.For<IThoughtProvider>();
        provider.Metadata.Returns(new DataProviderMetadata(
            name, "desc", null, color is null ? null : new BrandData(color, "")));
        provider.ExpectedWeight.Returns(weight);
        return provider;
    }

    [Fact]
    public void TotalWeight_IsSumOfProviderWeights()
    {
        SyncProgressReporter reporter = new([Provider("A", 3), Provider("B", 7)]);

        reporter.TotalWeight.Should().Be(10);
    }

    [Fact]
    public void BuildInitialEvent_ReportsZeroProgress()
    {
        SyncProgressReporter reporter = new([Provider("A", 5)]);

        string evt = reporter.BuildInitialEvent();

        evt.Should().StartWith("data: ").And.EndWith("\n\n");
        evt.Should().Contain("\"completed\":0").And.Contain("\"total\":5");
    }

    [Fact]
    public void OnTick_AccumulatesCompletedWeight()
    {
        SyncProgressReporter reporter = new([Provider("A", 10)]);

        reporter.OnTick(new SyncProgressTick(3, "A"));
        string evt = reporter.OnTick(new SyncProgressTick(4, "A"));

        reporter.Completed.Should().Be(7);
        evt.Should().Contain("\"completed\":7");
    }

    [Fact]
    public void OnTick_EmitsSegmentPerProvider_InWeightOrder()
    {
        SyncProgressReporter reporter = new([Provider("Big", 10, "#aaa"), Provider("Small", 2, "#bbb")]);

        string evt = reporter.OnTick(new SyncProgressTick(1, "Big"));

        int smallIdx = evt.IndexOf("Small", StringComparison.Ordinal);
        int bigIdx = evt.IndexOf("Big", StringComparison.Ordinal);
        smallIdx.Should().BeLessThan(bigIdx); // smaller weight comes first
        evt.Should().Contain("#aaa").And.Contain("#bbb");
    }

    [Fact]
    public void OnTick_ClampsCompletedToProviderTotal()
    {
        SyncProgressReporter reporter = new([Provider("A", 5)]);

        string evt = reporter.OnTick(new SyncProgressTick(100, "A")); // over-reports

        evt.Should().Contain("\"completed\":5"); // overall clamp
    }

    [Fact]
    public void BuildFinalEventIfIncomplete_ReturnsNull_WhenBarAtFull()
    {
        SyncProgressReporter reporter = new([Provider("A", 3)]);
        reporter.OnTick(new SyncProgressTick(3, "A"));

        reporter.BuildFinalEventIfIncomplete().Should().BeNull();
    }

    [Fact]
    public void BuildFinalEventIfIncomplete_Saturates_WhenBarIncomplete()
    {
        SyncProgressReporter reporter = new([Provider("A", 5), Provider("B", 5)]);
        reporter.OnTick(new SyncProgressTick(1, "A")); // far from done

        string? evt = reporter.BuildFinalEventIfIncomplete();

        evt.Should().NotBeNull();
        evt!.Should().Contain("\"completed\":10").And.Contain("\"total\":10");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 5000)]
    [InlineData(2, 10000)]
    [InlineData(3, 20000)]
    [InlineData(4, 40000)]
    [InlineData(5, 60000)] // capped
    [InlineData(10, 60000)] // still capped
    public void ComputeErrorBackoffMs_ExponentialCappedAt60s(int errors, int expectedMs)
    {
        SyncProgressReporter.ComputeErrorBackoffMs(errors).Should().Be(expectedMs);
    }
}