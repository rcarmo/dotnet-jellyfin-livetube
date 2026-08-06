using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveChannels.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>Materializes remote Invidious guide artwork as stable local files Jellyfin can serve.</summary>
public sealed class InvidiousArtworkCache
{
    private readonly HttpClient _httpClient;
    private readonly string _root;
    private readonly ILogger<InvidiousArtworkCache> _logger;

    /// <summary>Initializes the artwork cache.</summary>
    public InvidiousArtworkCache(HttpClient httpClient, string root, ILogger<InvidiousArtworkCache> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
        _root = root;
        _logger = logger;
    }

    /// <summary>Returns a local thumbnail path, downloading it through the configured Invidious instance when needed.</summary>
    public async Task<string?> GetThumbnailAsync(
        string instanceUrl,
        InvidiousFeedVideo video,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(video);
        if (string.IsNullOrWhiteSpace(video.VideoId))
        {
            return null;
        }

        try
        {
            var source = BuildProxyThumbnailUrl(instanceUrl, video);
            Directory.CreateDirectory(_root);
            var path = Path.Combine(_root, Hash(video.VideoId) + ".jpg");
            if (new FileInfo(path) is { Exists: true, Length: > 0 })
            {
                return path;
            }

            using var response = await _httpClient.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is null || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Invidious thumbnail response is not an image.");
            }

            var temporary = path + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
            try
            {
                var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, useAsync: true);
                try
                {
                    await response.Content.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    await destination.DisposeAsync().ConfigureAwait(false);
                }

                if (new FileInfo(temporary).Length == 0)
                {
                    throw new InvalidDataException("Invidious thumbnail response is empty.");
                }

                File.Move(temporary, path, overwrite: true);
                return path;
            }
            finally
            {
                File.Delete(temporary);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogWarning(ex, "Live Channels: could not cache the thumbnail for Invidious video {VideoId}", video.VideoId);
            return null;
        }
    }

    internal static Uri BuildProxyThumbnailUrl(string instanceUrl, InvidiousFeedVideo video)
    {
        if (!Uri.TryCreate(instanceUrl, UriKind.Absolute, out var root)
            || (root.Scheme != Uri.UriSchemeHttp && root.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("The Invidious instance must be an absolute HTTP or HTTPS URL.", nameof(instanceUrl));
        }

        var selected = video.VideoThumbnails
            .FindAll(t => t.Width > 0 && t.Height > 0 && !string.IsNullOrWhiteSpace(t.Url))
            .OrderByDescending(t => t.Width * (long)t.Height)
            .FirstOrDefault();
        if (selected is not null && Uri.TryCreate(root, selected.Url, out var supplied)
            && supplied.Scheme == root.Scheme && supplied.Host == root.Host && supplied.Port == root.Port)
        {
            return supplied;
        }

        var builder = new UriBuilder(root) { Query = string.Empty, Fragment = string.Empty };
        if (builder.Path.Length == 0 || builder.Path[^1] != '/')
        {
            builder.Path += "/";
        }

        return new Uri(builder.Uri, "vi/" + Uri.EscapeDataString(video.VideoId.Trim()) + "/maxres.jpg");
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
