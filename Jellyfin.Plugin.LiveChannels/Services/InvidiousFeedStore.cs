using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveChannels.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>Persists and merges recent Invidious subscription-feed entries independently of guide refreshes.</summary>
public sealed class InvidiousFeedStore : IDisposable
{
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(3);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly InvidiousFeedClient _client;
    private readonly ILogger<InvidiousFeedStore> _logger;
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Initializes the persistent feed store.</summary>
    public InvidiousFeedStore(InvidiousFeedClient client, IApplicationPaths paths, ILogger<InvidiousFeedStore> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _logger = logger;
        _root = Path.Combine(paths.DataPath, "livechannels", "invidious-feeds");
    }

    /// <summary>Returns the retained feed snapshot without making a network request.</summary>
    public IReadOnlyList<InvidiousFeedVideo> Get(string instanceUrl, DateTime utcNow)
        => Prune(Read(instanceUrl), utcNow);

    /// <summary>Returns a stable cache generation that changes whenever a retained feed file is rewritten.</summary>
    public string GenerationKey()
    {
        try
        {
            return Directory.Exists(_root)
                ? Directory.EnumerateFiles(_root, "*.json")
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .Select(path => Path.GetFileName(path) + ":" + File.GetLastWriteTimeUtc(path).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Aggregate("feeds", (key, part) => key + "|" + part)
                : "feeds-empty";
        }
        catch (IOException)
        {
            return "feeds-unavailable";
        }
    }

    /// <summary>Fetches, merges and atomically persists one source while retaining the prior snapshot on failure.</summary>
    public async Task<IReadOnlyList<InvidiousFeedVideo>> RefreshAsync(
        string instanceUrl,
        string token,
        int maximumResults,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = Read(instanceUrl);
            try
            {
                var fetched = await _client.GetFeedAsync(instanceUrl, token, maximumResults, cancellationToken).ConfigureAwait(false);
                var merged = Merge(existing, fetched, utcNow);
                await WriteAsync(instanceUrl, merged, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Live Channels: retained {Count} Invidious source videos from the last {Hours} hours for {Url}",
                    merged.Count,
                    (int)Retention.TotalHours,
                    instanceUrl);
                return merged;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var retained = Prune(existing, utcNow);
                _logger.LogError(ex, "Live Channels: could not refresh Invidious source {Url}; retaining {Count} cached videos", instanceUrl, retained.Count);
                return retained;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    internal static IReadOnlyList<InvidiousFeedVideo> Merge(
        IEnumerable<InvidiousFeedVideo> existing,
        IEnumerable<InvidiousFeedVideo> fetched,
        DateTime utcNow)
        => existing.Concat(fetched)
            .Where(video => !string.IsNullOrWhiteSpace(video.VideoId) && video.LengthSeconds > 0)
            .GroupBy(video => video.VideoId, StringComparer.Ordinal)
            .Select(group => group.Last())
            .Where(video => IsRetained(video, utcNow))
            .OrderByDescending(video => video.PublishedUtc ?? DateTime.UnixEpoch)
            .ThenBy(video => video.VideoId, StringComparer.Ordinal)
            .ToList();

    private static IReadOnlyList<InvidiousFeedVideo> Prune(IEnumerable<InvidiousFeedVideo> videos, DateTime utcNow)
        => Merge(Array.Empty<InvidiousFeedVideo>(), videos, utcNow);

    private static bool IsRetained(InvidiousFeedVideo video, DateTime utcNow)
        => video.PublishedUtc is { } published
            && published <= utcNow.AddMinutes(5)
            && published >= utcNow - Retention;

    private IReadOnlyList<InvidiousFeedVideo> Read(string instanceUrl)
    {
        try
        {
            var path = PathFor(instanceUrl);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<InvidiousFeedVideo>>(File.ReadAllText(path), JsonOptions) ?? new List<InvidiousFeedVideo>()
                : Array.Empty<InvidiousFeedVideo>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Live Channels: could not read the retained Invidious feed for {Url}", instanceUrl);
            return Array.Empty<InvidiousFeedVideo>();
        }
    }

    private async Task WriteAsync(string instanceUrl, IReadOnlyList<InvidiousFeedVideo> videos, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_root);
        var path = PathFor(instanceUrl);
        var temporary = path + ".tmp";
        var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, videos, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, path, overwrite: true);
    }

    private string PathFor(string instanceUrl)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instanceUrl.Trim().TrimEnd('/').ToLowerInvariant()))).ToLowerInvariant();
        return Path.Combine(_root, key + ".json");
    }
}
