using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveChannels.Services;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>Covers the minimal authenticated Invidious feed client.</summary>
public class InvidiousFeedClientTests
{
    [Fact]
    public async Task GetFeedAsync_SendsBearerTokenAndMapsVideos()
    {
        var handler = new StubHandler((request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("http://invidious.test/api/v1/auth/feed?max_results=25&page=1", request.RequestUri?.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("account-token", request.Headers.Authorization?.Parameter);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "notifications": [],
                      "videos": [{
                        "videoId": "abc123",
                        "title": "A useful video",
                        "author": "Example Channel",
                        "authorId": "UC123",
                        "published": 1786000000,
                        "lengthSeconds": 321,
                        "videoThumbnails": [{
                          "quality": "medium",
                          "url": "https://img.test/abc123.jpg",
                          "width": 320,
                          "height": 180
                        }]
                      }]
                    }
                    """)
            });
        });
        var client = new InvidiousFeedClient(new HttpClient(handler));

        var videos = await client.GetFeedAsync("http://invidious.test", "account-token", 25, CancellationToken.None);

        var video = Assert.Single(videos);
        Assert.Equal("abc123", video.VideoId);
        Assert.Equal("A useful video", video.Title);
        Assert.Equal("Example Channel", video.Author);
        Assert.Equal("UC123", video.AuthorId);
        Assert.Equal(321, video.LengthSeconds);
        Assert.NotNull(video.PublishedUtc);
        Assert.Equal("https://img.test/abc123.jpg", Assert.Single(video.VideoThumbnails).Url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("ftp://invidious.test")]
    public async Task GetFeedAsync_RejectsInvalidInstanceUrl(string instanceUrl)
    {
        var client = new InvidiousFeedClient(new HttpClient(new StubHandler((_, _) => throw new InvalidOperationException())));

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetFeedAsync(instanceUrl, "token", 25, CancellationToken.None));
    }

    [Fact]
    public async Task GetFeedAsync_RejectsMissingToken()
    {
        var client = new InvidiousFeedClient(new HttpClient(new StubHandler((_, _) => throw new InvalidOperationException())));

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetFeedAsync("https://invidious.test", " ", 25, CancellationToken.None));
    }

    [Fact]
    public async Task GetFeedAsync_PropagatesAuthenticationFailure()
    {
        var client = new InvidiousFeedClient(new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)))));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetFeedAsync("https://invidious.test", "wrong-token", 25, CancellationToken.None));
    }

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
