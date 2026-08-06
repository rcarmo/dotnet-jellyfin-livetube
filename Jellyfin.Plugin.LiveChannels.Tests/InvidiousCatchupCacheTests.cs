using System;
using System.IO;
using Jellyfin.Plugin.LiveChannels.Services;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>Covers short-lived Invidious control-manifest cleanup.</summary>
public sealed class InvidiousCatchupCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "livechannels-catchup-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Cleanup_RemovesExpiredManifestsAndProtectsCurrentManifest()
    {
        Directory.CreateDirectory(_root);
        var old = Write("old.mpd", DateTime.UtcNow.AddHours(-3));
        var current = Write("current.mpd", DateTime.UtcNow.AddHours(-3));
        var fresh = Write("fresh.mpd", DateTime.UtcNow);

        InvidiousCatchupManifest.Cleanup(_root, TimeSpan.FromHours(1), current);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(current));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Cleanup_IgnoresMediaPayloadFiles()
    {
        Directory.CreateDirectory(_root);
        var media = Write("payload.mp4", DateTime.UtcNow.AddDays(-1));

        InvidiousCatchupManifest.Cleanup(_root, TimeSpan.FromHours(1), string.Empty);

        Assert.True(File.Exists(media));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Write(string name, DateTime written)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "<MPD />");
        File.SetLastWriteTimeUtc(path, written);
        return path;
    }
}
