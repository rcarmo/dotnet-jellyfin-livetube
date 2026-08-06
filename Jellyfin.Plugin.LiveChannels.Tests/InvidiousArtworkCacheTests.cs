using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>Covers stable local materialization of remote Invidious guide artwork.</summary>
public sealed class InvidiousArtworkCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "livechannels-artwork-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void BuildProxyThumbnailUrl_AcceptsOnlyTheConfiguredInstance()
    {
        var video = Video("abc123", "http://invidious.test/vi/abc123/maxres.jpg");

        var url = InvidiousArtworkCache.BuildProxyThumbnailUrl("http://invidious.test", video);

        Assert.Equal("http://invidious.test/vi/abc123/maxres.jpg", url.ToString());
    }

    [Fact]
    public void BuildProxyThumbnailUrl_RewritesThirdPartyArtworkThroughInvidious()
    {
        var video = Video("abc123", "https://i.ytimg.com/vi/abc123/maxresdefault.jpg");

        var url = InvidiousArtworkCache.BuildProxyThumbnailUrl("http://invidious.test", video);

        Assert.Equal("http://invidious.test/vi/abc123/maxres.jpg", url.ToString());
    }

    [Fact]
    public async Task GetThumbnailAsync_WritesOnceAndReturnsStableLocalPath()
    {
        var requests = 0;
        var client = new HttpClient(new StubHandler((request, _) =>
        {
            requests++;
            Assert.Equal("http://invidious.test/vi/abc123/maxres.jpg", request.RequestUri?.ToString());
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 0xff, 0xd8, 0xff, 0xd9 })
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            return Task.FromResult(response);
        }));
        var cache = new InvidiousArtworkCache(client, _root, NullLogger<InvidiousArtworkCache>.Instance);
        var video = Video("abc123", "http://invidious.test/vi/abc123/maxres.jpg");

        var first = await cache.GetThumbnailAsync("http://invidious.test", video, CancellationToken.None);
        var second = await cache.GetThumbnailAsync("http://invidious.test", video, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.True(File.Exists(first));
        Assert.Equal(4, new FileInfo(first).Length);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task GetThumbnailAsync_RejectsNonImageResponses()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not an image") };
        var cache = new InvidiousArtworkCache(
            new HttpClient(new StubHandler((_, _) => Task.FromResult(response))),
            _root,
            NullLogger<InvidiousArtworkCache>.Instance);

        var result = await cache.GetThumbnailAsync("http://invidious.test", Video("abc123", "http://invidious.test/vi/abc123/maxres.jpg"), CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(Directory.Exists(_root) ? Directory.GetFiles(_root) : Array.Empty<string>());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static InvidiousFeedVideo Video(string id, string url)
        => new()
        {
            VideoId = id,
            VideoThumbnails = { new InvidiousThumbnail { Url = url, Width = 1280, Height = 720 } }
        };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        {
            _send = send;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _send(request, cancellationToken);
    }
}
