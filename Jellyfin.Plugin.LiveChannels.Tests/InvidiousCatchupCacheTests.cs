using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.LiveChannels.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>Covers finite Invidious media remux arguments and cache retention.</summary>
public sealed class InvidiousCatchupCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "livechannels-catchup-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void BuildRemuxArguments_MapsVideoAndOriginalAudioWithoutReencoding()
    {
        var media = new InvidiousPlaybackMedia("https://video.test/v", "https://audio.test/original", "en", 1080);

        var arguments = InvidiousCatchupCache.BuildRemuxArguments(media, "/cache/video.mp4");

        Assert.Equal(2, arguments.Count(value => value == "-i"));
        Assert.Contains("https://video.test/v", arguments);
        Assert.Contains("https://audio.test/original", arguments);
        Assert.Contains("0:v:0", arguments);
        Assert.Contains("1:a:0", arguments);
        Assert.Contains("copy", arguments);
        Assert.Contains("+faststart", arguments);
        Assert.Equal("/cache/video.mp4", arguments[^1]);
    }

    [Fact]
    public void Evict_RemovesOldestFilesUntilBelowLimitAndProtectsCurrentFile()
    {
        Directory.CreateDirectory(_root);
        var old = Write("old.mp4", 8, DateTime.UtcNow.AddHours(-3));
        var middle = Write("middle.mp4", 8, DateTime.UtcNow.AddHours(-2));
        var current = Write("current.mp4", 8, DateTime.UtcNow.AddHours(-1));

        InvidiousCatchupCache.Evict(_root, 12, current, NullLogger.Instance);

        Assert.False(File.Exists(old));
        Assert.False(File.Exists(middle));
        Assert.True(File.Exists(current));
    }

    [Fact]
    public void Evict_LeavesFilesWhenAlreadyWithinLimit()
    {
        Directory.CreateDirectory(_root);
        var first = Write("first.mp4", 4, DateTime.UtcNow.AddHours(-2));
        var second = Write("second.mp4", 4, DateTime.UtcNow.AddHours(-1));

        InvidiousCatchupCache.Evict(_root, 8, second, NullLogger.Instance);

        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Write(string name, int bytes, DateTime access)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastAccessTimeUtc(path, access);
        File.SetLastWriteTimeUtc(path, access);
        return path;
    }
}
