using System;
using System.IO;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Services;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
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
        var placeholder = Assert.Single(item.MediaSources);
        Assert.Equal(item.Id, placeholder.Id);
        Assert.Equal(MediaSourceType.Placeholder, placeholder.Type);
        Assert.Equal(MediaProtocol.Http, placeholder.Protocol);
        Assert.StartsWith("livechannels-catchup://", placeholder.Path, StringComparison.Ordinal);
        Assert.False(placeholder.SupportsDirectPlay);
        Assert.False(placeholder.SupportsDirectStream);
        Assert.True(placeholder.SupportsTranscoding);
        var source = CatchupChannel.BuildMediaSource("channel-1", slot, "/media/example.mkv");
        Assert.Equal("/media/example.mkv", source.Path);
        Assert.Equal(MediaProtocol.File, source.Protocol);
        Assert.False(source.IsInfiniteStream);
        Assert.Equal(program.DurationTicks, source.RunTimeTicks);
        Assert.True(source.SupportsDirectPlay);
        Assert.True(source.SupportsDirectStream);
        Assert.Equal(placeholder.Id, source.Id);
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

        Assert.Equal("dash", source.Container);
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
    public void SelectVisibleSlots_DeduplicatesInvidiousSources_ButKeepsLocalAirings()
    {
        var first = new ProgramEntry(Guid.Parse("11111111-2222-3333-4444-555555555555"), "Remote", null, TimeSpan.FromMinutes(10).Ticks, "video-id") { IsInvidious = true };
        var sameTitleDifferentVideo = new ProgramEntry(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), "Remote", null, TimeSpan.FromMinutes(10).Ticks, "other-video") { IsInvidious = true };
        var local = new ProgramEntry(Guid.Parse("99999999-8888-7777-6666-555555555555"), "Local", null, TimeSpan.FromMinutes(10).Ticks, "/media/local.mkv");
        var timeline = new[]
        {
            new ScheduledProgram(first, DateTime.UnixEpoch, DateTime.UnixEpoch.AddMinutes(10)),
            new ScheduledProgram(local, DateTime.UnixEpoch.AddMinutes(10), DateTime.UnixEpoch.AddMinutes(20)),
            new ScheduledProgram(first, DateTime.UnixEpoch.AddMinutes(20), DateTime.UnixEpoch.AddMinutes(30)),
            new ScheduledProgram(local, DateTime.UnixEpoch.AddMinutes(30), DateTime.UnixEpoch.AddMinutes(40)),
            new ScheduledProgram(sameTitleDifferentVideo, DateTime.UnixEpoch.AddMinutes(40), DateTime.UnixEpoch.AddMinutes(50))
        };

        var selected = CatchupChannel.SelectVisibleSlots(timeline, DateTime.UnixEpoch.AddHours(1), _ => true);

        Assert.Equal(4, selected.Count);
        Assert.Equal(sameTitleDifferentVideo.ItemId, selected[0].Program.ItemId);
        Assert.Equal(local.ItemId, selected[1].Program.ItemId);
        Assert.Equal(first.ItemId, selected[2].Program.ItemId);
        Assert.Equal(DateTime.UnixEpoch.AddMinutes(20), selected[2].Start);
        Assert.Equal(local.ItemId, selected[3].Program.ItemId);
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
