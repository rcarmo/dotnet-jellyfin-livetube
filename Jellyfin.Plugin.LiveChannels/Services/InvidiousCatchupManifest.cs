using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>A local control manifest plus the concrete stream metadata Jellyfin needs before transcoding.</summary>
public sealed record InvidiousCatchupManifestResult(string Path, string? AudioLanguage, int VideoHeight);

/// <summary>Writes short-lived local DASH control manifests for remote Invidious catch-up playback.</summary>
public sealed class InvidiousCatchupManifest
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _published = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _publishedMedia = new(StringComparer.Ordinal);
    private static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan CleanupAge = TimeSpan.FromHours(1);
    private readonly InvidiousFeedClient _invidious;
    private readonly IServerApplicationHost _applicationHost;
    private readonly string _root;

    /// <summary>Initializes the manifest store.</summary>
    public InvidiousCatchupManifest(InvidiousFeedClient invidious, IApplicationPaths paths, IServerApplicationHost applicationHost)
    {
        ArgumentNullException.ThrowIfNull(invidious);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(applicationHost);
        _invidious = invidious;
        _applicationHost = applicationHost;
        _root = Path.Combine(paths.CachePath, "livechannels-assets", "catchup-manifests");
    }

    /// <summary>
    /// Returns a fresh local MPD that references remote video and original-audio representations. The MPD contains
    /// no media payload and is replaced before its signed representation URLs become stale.
    /// </summary>
    public async Task<InvidiousCatchupManifestResult> GetAsync(string instanceUrl, string videoId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceUrl);
        var key = SanitizeVideoId(videoId);
        var gate = Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_root);
            var destination = Path.Combine(_root, key + ".mpd");
            var media = await _invidious.ResolveOriginalPlaybackMediaAsync(instanceUrl, videoId, cancellationToken).ConfigureAwait(false);
            if (File.Exists(destination) && DateTime.UtcNow - File.GetLastWriteTimeUtc(destination) < MaximumAge)
            {
                return Publish(destination, media);
            }

            var temporary = destination + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
            try
            {
                await _invidious.WriteOriginalAudioDashManifestAsync(instanceUrl, videoId, temporary, cancellationToken).ConfigureAwait(false);
                RewriteMediaUrls(temporary);
                if (new FileInfo(temporary).Length is <= 0 or > 2 * 1024 * 1024)
                {
                    throw new InvalidDataException("The Invidious DASH control manifest has an invalid size.");
                }

                File.Move(temporary, destination, overwrite: true);
                Cleanup(_root, CleanupAge, destination);
                return Publish(destination, media);
            }
            finally
            {
                TryDelete(temporary);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Resolves an opaque, short-lived HTTP publication token to its control manifest.</summary>
    public string? ResolvePublishedPath(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length != 32 || token.Any(character => !char.IsAsciiHexDigit(character)))
        {
            return null;
        }

        return _published.TryGetValue(token, out var path) ? path : null;
    }

    /// <summary>Resolves an opaque media proxy token to the signed representation URL selected from Invidious.</summary>
    public string? ResolvePublishedMediaUrl(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length != 32 || token.Any(character => !char.IsAsciiHexDigit(character)))
        {
            return null;
        }

        return _publishedMedia.TryGetValue(token, out var url) ? url : null;
    }

    internal static void Cleanup(string root, TimeSpan maximumAge, string protectedPath)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - maximumAge;
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*.mpd").Where(file => file.LastWriteTimeUtc < cutoff))
        {
            if (!string.Equals(file.FullName, protectedPath, StringComparison.Ordinal))
            {
                TryDelete(file.FullName);
            }
        }
    }

    private void RewriteMediaUrls(string path)
    {
        var document = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        var ns = document.Root?.Name.Namespace ?? XNamespace.None;
        foreach (var baseUrl in document.Descendants(ns + "BaseURL"))
        {
            var remote = baseUrl.Value.Trim();
            if (!Uri.TryCreate(remote, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidDataException("The catch-up DASH manifest contains an invalid media URL.");
            }

            var token = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            _publishedMedia[token] = remote;
            baseUrl.Value = _applicationHost.GetApiUrlForLocalAccess() + "/livechannels/catchup-media/" + token;
        }

        document.Save(path, SaveOptions.DisableFormatting);
    }

    private InvidiousCatchupManifestResult Publish(string path, InvidiousPlaybackMedia media)
    {
        var existing = _published.FirstOrDefault(entry => string.Equals(entry.Value, path, StringComparison.Ordinal));
        var token = string.IsNullOrEmpty(existing.Key) ? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) : existing.Key;
        _published[token] = path;
        var url = _applicationHost.GetApiUrlForLocalAccess() + "/livechannels/catchup-manifest/" + token + ".mpd";
        return new InvidiousCatchupManifestResult(url, media.AudioLanguage, media.VideoHeight);
    }

    private static string SanitizeVideoId(string videoId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoId);
        var value = videoId.Trim();
        if (value.Length is < 1 or > 64 || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("The Invidious video ID contains invalid characters.", nameof(videoId));
        }

        return value;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
