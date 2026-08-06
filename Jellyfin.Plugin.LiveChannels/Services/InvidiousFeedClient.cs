using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Xml.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveChannels.Models;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>The exact media representations selected for one Invidious video.</summary>
public sealed record InvidiousPlaybackMedia(string VideoUrl, string AudioUrl, string? AudioLanguage, int VideoHeight);

/// <summary>Reads one Invidious account's authenticated subscription feed.</summary>
public sealed class InvidiousFeedClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _httpClient;

    /// <summary>Initializes a new instance of the <see cref="InvidiousFeedClient"/> class.</summary>
    /// <param name="httpClient">The HTTP client used for requests.</param>
    public InvidiousFeedClient(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    /// <summary>Gets recent videos from the account represented by <paramref name="token"/>.</summary>
    /// <param name="instanceUrl">The root URL of the Invidious instance.</param>
    /// <param name="token">A bearer token scoped to <c>GET:feed</c>.</param>
    /// <param name="maximumResults">The bounded number of feed videos to request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The account's recent subscription videos.</returns>
    public async Task<IReadOnlyList<InvidiousFeedVideo>> GetFeedAsync(
        string instanceUrl,
        string token,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        var root = ParseInstanceUrl(instanceUrl);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("An Invidious API token is required.", nameof(token));
        }

        if (maximumResults is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResults), "Maximum results must be between 1 and 200.");
        }

        var endpoint = new Uri(
            root,
            "api/v1/auth/feed?max_results=" + maximumResults.ToString(CultureInfo.InvariantCulture) + "&page=1");
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var feed = await JsonSerializer.DeserializeAsync<InvidiousFeedResponse>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        return feed is null ? Array.Empty<InvidiousFeedVideo>() : feed.Videos;
    }

    /// <summary>Builds the local Invidious DASH manifest URL for one stable video id.</summary>
    /// <param name="instanceUrl">The instance root URL.</param>
    /// <param name="videoId">The stable YouTube video id.</param>
    /// <returns>A just-in-time DASH manifest URL.</returns>
    public static string BuildDashUrl(string instanceUrl, string videoId)
    {
        var root = ParseInstanceUrl(instanceUrl);
        if (string.IsNullOrWhiteSpace(videoId))
        {
            throw new ArgumentException("An Invidious video id is required.", nameof(videoId));
        }

        return new Uri(root, "api/manifest/dash/id/" + Uri.EscapeDataString(videoId.Trim()) + "?local=true").ToString();
    }

    /// <summary>Resolves one explicit video representation and the original audio representation.</summary>
    public async Task<InvidiousPlaybackMedia> ResolveOriginalPlaybackMediaAsync(
        string instanceUrl,
        string videoId,
        CancellationToken cancellationToken)
    {
        var manifestUrl = BuildDashUrl(instanceUrl, videoId);
        using var response = await _httpClient.GetAsync(manifestUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var document = XDocument.Parse(xml);
        var ns = document.Root?.Name.Namespace ?? XNamespace.None;
        var audioSets = document.Descendants(ns + "AdaptationSet").Where(IsAudioAdaptation).ToList();
        var original = SelectOriginalAudio(audioSets, ns);
        var baseUri = response.RequestMessage?.RequestUri ?? new Uri(manifestUrl);
        var audioUrl = ResolveBaseUrl(original.Descendants(ns + "BaseURL").FirstOrDefault(), baseUri, "original audio");

        var video = document.Descendants(ns + "AdaptationSet")
            .Where(a => !IsAudioAdaptation(a))
            .SelectMany(a => a.Elements(ns + "Representation"))
            .Where(r => ((string?)r.Attribute("codecs"))?.StartsWith("avc1", StringComparison.OrdinalIgnoreCase) == true)
            .Select(r => new
            {
                Element = r,
                Height = (int?)r.Attribute("height") ?? 0,
                Bandwidth = (long?)r.Attribute("bandwidth") ?? 0
            })
            .Where(r => r.Height is > 0 and <= 1080)
            .OrderByDescending(r => r.Height)
            .ThenByDescending(r => r.Bandwidth)
            .FirstOrDefault()
            ?? throw new InvalidDataException("Invidious DASH manifest has no H.264 video representation up to 1080p.");
        var videoUrl = ResolveBaseUrl(video.Element.Element(ns + "BaseURL"), baseUri, "video");
        return new InvidiousPlaybackMedia(videoUrl, audioUrl, (string?)original.Attribute("lang"), video.Height);
    }

    /// <summary>
    /// Downloads a video's DASH manifest and writes a playback manifest containing only its explicitly marked
    /// original audio adaptation set. YouTube places auto-dubbed tracks before the original in some manifests;
    /// leaving selection to ffmpeg can therefore play an arbitrary dub even with <c>-map 0:a:0</c>.
    /// </summary>
    /// <param name="instanceUrl">The instance root URL.</param>
    /// <param name="videoId">The stable YouTube video id.</param>
    /// <param name="destinationPath">The local path to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The selected audio language, when declared by the manifest.</returns>
    public async Task<string?> WriteOriginalAudioDashManifestAsync(
        string instanceUrl,
        string videoId,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var manifestUrl = BuildDashUrl(instanceUrl, videoId);
        using var response = await _httpClient.GetAsync(manifestUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var ns = document.Root?.Name.Namespace ?? XNamespace.None;
        var audioSets = document.Descendants(ns + "AdaptationSet")
            .Where(IsAudioAdaptation)
            .ToList();
        if (audioSets.Count == 0)
        {
            throw new InvalidDataException("Invidious DASH manifest has no audio adaptation set.");
        }

        var original = SelectOriginalAudio(audioSets, ns);

        foreach (var alternate in audioSets.Where(a => !ReferenceEquals(a, original)).ToList())
        {
            alternate.Remove();
        }

        // A downloaded MPD would otherwise resolve its relative /companion/videoplayback BaseURLs against the
        // local file path. Make every media URL absolute against the final redirected manifest URI.
        var baseUri = response.RequestMessage?.RequestUri ?? new Uri(manifestUrl);
        foreach (var baseUrl in document.Descendants(ns + "BaseURL"))
        {
            if (Uri.TryCreate(baseUri, baseUrl.Value.Trim(), out var absolute))
            {
                // Keep the URL as normal XML text. The DASH demuxer decodes &amp; back to '&'; CDATA makes its
                // XML reader drop the ampersands and concatenate signed query parameters, yielding HTTP 403.
                baseUrl.Value = absolute.ToString();
            }
        }

        using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.Read, 8192, useAsync: true);
        await document.SaveAsync(destination, SaveOptions.DisableFormatting, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        return (string?)original.Attribute("lang");
    }

    private static XElement SelectOriginalAudio(List<XElement> audioSets, XNamespace ns)
        => audioSets
            .FirstOrDefault(a => HasRole(a, ns, "main") && !HasRole(a, ns, "enhanced-audio-intelligibility"))
            ?? audioSets.FirstOrDefault(a => HasRole(a, ns, "main"))
            ?? audioSets.Where(a => !HasRole(a, ns, "alternate") && !HasRole(a, ns, "dub")).SingleOrDefault()
            ?? (audioSets.Count == 1 ? audioSets[0] : null)
            ?? throw new InvalidDataException("Invidious DASH manifest does not identify one original audio track.");

    private static string ResolveBaseUrl(XElement? element, Uri baseUri, string kind)
    {
        if (element is null || !Uri.TryCreate(baseUri, element.Value.Trim(), out var absolute))
        {
            throw new InvalidDataException("Invidious DASH manifest has no valid " + kind + " URL.");
        }

        return absolute.ToString();
    }

    private static bool IsAudioAdaptation(XElement adaptation)
        => string.Equals((string?)adaptation.Attribute("contentType"), "audio", StringComparison.OrdinalIgnoreCase)
            || ((string?)adaptation.Attribute("mimeType"))?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true;

    private static bool HasRole(XElement adaptation, XNamespace ns, string role)
        => adaptation.Elements(ns + "Role")
            .Any(r => string.Equals((string?)r.Attribute("value"), role, StringComparison.OrdinalIgnoreCase));

    private static Uri ParseInstanceUrl(string instanceUrl)
    {
        if (!Uri.TryCreate(instanceUrl, UriKind.Absolute, out var root)
            || (root.Scheme != Uri.UriSchemeHttp && root.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("The Invidious instance must be an absolute HTTP or HTTPS URL.", nameof(instanceUrl));
        }

        var builder = new UriBuilder(root) { Query = string.Empty, Fragment = string.Empty };
        if (builder.Path.Length == 0 || builder.Path[^1] != '/')
        {
            builder.Path += "/";
        }

        return builder.Uri;
    }

    private sealed class InvidiousFeedResponse
    {
        public List<InvidiousFeedVideo> Videos { get; set; } = new();
    }
}
