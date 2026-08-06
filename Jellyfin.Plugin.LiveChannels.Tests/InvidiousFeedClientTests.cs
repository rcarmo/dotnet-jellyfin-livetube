using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Xml.Linq;
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

    [Fact]
    public void BuildDashUrl_UsesStableVideoIdAndLocalProxy()
    {
        Assert.Equal(
            "http://invidious.test/api/manifest/dash/id/abc123?local=true",
            InvidiousFeedClient.BuildDashUrl("http://invidious.test", "abc123"));
    }

    [Fact]
    public async Task ResolveOriginalPlaybackMediaAsync_SelectsMainAudioAndBestH264Video()
    {
        const string mpd = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011"><Period>
              <AdaptationSet contentType="audio" lang="hi"><Role value="dub" /><Representation><BaseURL>/dub.m4a?a=1&amp;b=2</BaseURL></Representation></AdaptationSet>
              <AdaptationSet contentType="audio" lang="en-US"><Role value="main" /><Representation><BaseURL>/original.m4a?a=1&amp;b=2</BaseURL></Representation></AdaptationSet>
              <AdaptationSet contentType="video">
                <Representation codecs="vp09" height="2160" bandwidth="9000"><BaseURL>/vp9.webm</BaseURL></Representation>
                <Representation codecs="avc1.640028" height="1080" bandwidth="5000"><BaseURL>/1080.mp4</BaseURL></Representation>
                <Representation codecs="avc1.4d401f" height="720" bandwidth="3000"><BaseURL>/720.mp4</BaseURL></Representation>
              </AdaptationSet>
            </Period></MPD>
            """;
        var client = new InvidiousFeedClient(new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(mpd)
        }))));

        var media = await client.ResolveOriginalPlaybackMediaAsync("http://invidious.test", "abc123", CancellationToken.None);

        Assert.Equal("en-US", media.AudioLanguage);
        Assert.Equal("http://invidious.test/original.m4a?a=1&b=2", media.AudioUrl);
        Assert.Equal("http://invidious.test/1080.mp4", media.VideoUrl);
        Assert.Equal(1080, media.VideoHeight);
    }

    [Fact]
    public async Task WriteOriginalAudioDashManifestAsync_RemovesDubsAndKeepsMainAudio()
    {
        const string mpd = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011">
              <Period>
                <AdaptationSet contentType="audio" lang="hi">
                  <Role schemeIdUri="urn:mpeg:dash:role:2011" value="alternate" />
                  <Role schemeIdUri="urn:mpeg:dash:role:2011" value="dub" />
                  <Representation id="140-hi"><BaseURL>/dub.m4a</BaseURL></Representation>
                </AdaptationSet>
                <AdaptationSet contentType="audio" lang="en-US">
                  <Role schemeIdUri="urn:mpeg:dash:role:2011" value="main" />
                  <Representation id="140-en"><BaseURL>/original.m4a</BaseURL></Representation>
                </AdaptationSet>
                <AdaptationSet contentType="video">
                  <Representation id="137"><BaseURL>/video.mp4</BaseURL></Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """;
        var client = new InvidiousFeedClient(new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(mpd)
        }))));
        var path = Path.GetTempFileName();
        try
        {
            var language = await client.WriteOriginalAudioDashManifestAsync("http://invidious.test", "abc123", path, CancellationToken.None);
            var document = XDocument.Load(path);
            var ns = document.Root!.Name.Namespace;
            var audio = document.Descendants(ns + "AdaptationSet").Where(e => (string?)e.Attribute("contentType") == "audio").ToList();

            Assert.Equal("en-US", language);
            Assert.Single(audio);
            Assert.Equal("en-US", (string?)audio[0].Attribute("lang"));
            var audioBaseUrl = audio[0].Descendants(ns + "BaseURL").Single();
            Assert.Equal("http://invidious.test/original.m4a", audioBaseUrl.Value);
            Assert.IsType<XCData>(audioBaseUrl.FirstNode);
            Assert.Equal("http://invidious.test/video.mp4", document.Descendants(ns + "AdaptationSet").Single(e => (string?)e.Attribute("contentType") == "video").Descendants(ns + "BaseURL").Single().Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task WriteOriginalAudioDashManifestAsync_RejectsAmbiguousDubOnlyManifest()
    {
        const string mpd = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011"><Period>
              <AdaptationSet contentType="audio" lang="hi"><Role value="dub" /><Representation id="hi" /></AdaptationSet>
              <AdaptationSet contentType="audio" lang="de"><Role value="dub" /><Representation id="de" /></AdaptationSet>
              <AdaptationSet contentType="video"><Representation id="video" /></AdaptationSet>
            </Period></MPD>
            """;
        var client = new InvidiousFeedClient(new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(mpd)
        }))));
        var path = Path.GetTempFileName();
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => client.WriteOriginalAudioDashManifestAsync("http://invidious.test", "abc123", path, CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
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
