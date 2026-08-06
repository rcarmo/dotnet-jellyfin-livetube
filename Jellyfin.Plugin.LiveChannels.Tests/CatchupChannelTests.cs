using System;
using System.IO;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Services;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>Covers finite VOD projection of scheduled local media.</summary>
public sealed class CatchupChannelTests
{
    [Fact]
    public void BuildLocalItem_ProducesFiniteSeekableFileSource()
    {
        var start = new DateTime(2026, 8, 6, 14, 0, 0, DateTimeKind.Utc);
        var program = new ProgramEntry(Guid.Parse("11111111-2222-3333-4444-555555555555"), "Example", "Overview", TimeSpan.FromMinutes(42).Ticks, "/media/example.mkv")
        {
            SeriesName = "Series",
            SeasonNumber = 2,
            EpisodeNumber = 3,
            ThumbImagePath = "/art/thumb.jpg"
        };
        var slot = new ScheduledProgram(program, start, start.AddMinutes(42));

        var item = CatchupChannel.BuildItem("channel-1", slot);

        Assert.Equal(ChannelItemType.Media, item.Type);
        Assert.Equal(ChannelMediaType.Video, item.MediaType);
        Assert.False(item.IsLiveStream);
        Assert.Equal(program.DurationTicks, item.RunTimeTicks);
        Assert.Equal("/art/thumb.jpg", item.ImageUrl);
        Assert.Empty(item.MediaSources);
        var source = CatchupChannel.BuildMediaSource("channel-1", slot, "/media/example.mkv");
        Assert.Equal("/media/example.mkv", source.Path);
        Assert.Equal(MediaProtocol.File, source.Protocol);
        Assert.False(source.IsInfiniteStream);
        Assert.Equal(program.DurationTicks, source.RunTimeTicks);
        Assert.True(source.SupportsDirectPlay);
        Assert.True(source.SupportsDirectStream);
    }

    [Fact]
    public void BuildInvidiousMediaSource_DeclaresRemoteManifestStreams()
    {
        var start = new DateTime(2026, 8, 6, 14, 0, 0, DateTimeKind.Utc);
        var program = new ProgramEntry(Guid.Parse("11111111-2222-3333-4444-555555555555"), "Remote", "Overview", TimeSpan.FromMinutes(12).Ticks, "video-id")
        {
            IsInvidious = true,
            InvidiousUrl = "http://invidious.test"
        };
        var slot = new ScheduledProgram(program, start, start.AddMinutes(12));

        var source = CatchupChannel.BuildInvidiousMediaSource(
            "channel-80",
            slot,
            new InvidiousCatchupManifestResult("/cache/video-id.mpd", "en-US", 1080));

        Assert.Equal("mpd", source.Container);
        Assert.False(source.IsInfiniteStream);
        Assert.False(source.SupportsProbing);
        Assert.False(source.SupportsDirectPlay);
        Assert.False(source.SupportsDirectStream);
        Assert.Collection(
            source.MediaStreams,
            video =>
            {
                Assert.Equal(MediaStreamType.Video, video.Type);
                Assert.Equal("h264", video.Codec);
                Assert.Equal(1920, video.Width);
                Assert.Equal(1080, video.Height);
            },
            audio =>
            {
                Assert.Equal(MediaStreamType.Audio, audio.Type);
                Assert.Equal("aac", audio.Codec);
                Assert.Equal("en-US", audio.Language);
                Assert.True(audio.IsDefault);
            });
    }

    [Fact]
    public void StableIds_SeparateFoldersAndAirings()
    {
        var item = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var first = CatchupChannel.ItemId("channel-1", DateTime.UnixEpoch, item);
        var repeated = CatchupChannel.ItemId("channel-1", DateTime.UnixEpoch, item);
        var later = CatchupChannel.ItemId("channel-1", DateTime.UnixEpoch.AddHours(1), item);

        Assert.Equal(first, repeated);
        Assert.NotEqual(first, later);
        Assert.NotEqual(CatchupChannel.FolderId("channel-1"), CatchupChannel.FolderId("channel-2"));
    }
}
