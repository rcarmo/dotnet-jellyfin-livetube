using System;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Services;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>Covers the three-day retained Invidious source window.</summary>
public sealed class InvidiousFeedStoreTests
{
    private static readonly DateTime Now = new(2026, 8, 6, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Merge_DeduplicatesByVideoId_AndPrefersFetchedMetadata()
    {
        var existing = new[] { Video("same", "Old", Now.AddHours(-2)) };
        var fetched = new[] { Video("same", "New", Now.AddHours(-2)), Video("other", "Other", Now.AddHours(-1)) };

        var merged = InvidiousFeedStore.Merge(existing, fetched, Now);

        Assert.Collection(
            merged,
            first => Assert.Equal("other", first.VideoId),
            second =>
            {
                Assert.Equal("same", second.VideoId);
                Assert.Equal("New", second.Title);
            });
    }

    [Fact]
    public void Merge_RetainsExactlyThreeDays_AndDropsOlderOrInvalidItems()
    {
        var merged = InvidiousFeedStore.Merge(
            Array.Empty<InvidiousFeedVideo>(),
            new[]
            {
                Video("fresh", "Fresh", Now.AddHours(-1)),
                Video("boundary", "Boundary", Now.AddDays(-3)),
                Video("old", "Old", Now.AddDays(-3).AddSeconds(-1)),
                Video("future", "Future", Now.AddMinutes(6)),
                Video("zero", "Zero", Now.AddHours(-1), length: 0)
            },
            Now);

        Assert.Collection(
            merged,
            first => Assert.Equal("fresh", first.VideoId),
            second => Assert.Equal("boundary", second.VideoId));
    }

    private static InvidiousFeedVideo Video(string id, string title, DateTime published, long length = 60)
        => new()
        {
            VideoId = id,
            Title = title,
            LengthSeconds = length,
            Published = new DateTimeOffset(published).ToUnixTimeSeconds()
        };
}
