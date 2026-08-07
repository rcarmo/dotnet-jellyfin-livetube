using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveChannels.Utilities;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>Covers validation and replacement of persistent channel-logo cache files.</summary>
public sealed class LogoCacheTests
{
    [Fact]
    public void IsUsable_RejectsMissingAndEmptyFiles()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "logo.png");
            Assert.False(LogoCache.IsUsable(path));
            File.WriteAllBytes(path, Array.Empty<byte>());
            Assert.False(LogoCache.IsUsable(path));
            File.WriteAllBytes(path, new byte[] { 1 });
            Assert.True(LogoCache.IsUsable(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAsync_ReplacesEmptyFileWithCompletedPayload()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "logo.png");
            File.WriteAllBytes(path, Array.Empty<byte>());

            await LogoCache.WriteAsync(path, new byte[] { 1, 2, 3, 4 }, CancellationToken.None);

            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(path));
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAsync_RejectsEmptyPayload()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "logo.png");
            await Assert.ThrowsAsync<ArgumentException>(() => LogoCache.WriteAsync(path, Array.Empty<byte>(), CancellationToken.None));
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "livechannels-logo-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
