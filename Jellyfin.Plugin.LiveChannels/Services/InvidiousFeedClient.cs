using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveChannels.Models;

namespace Jellyfin.Plugin.LiveChannels.Services;

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
