using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveChannels.Models;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>Refreshes retained Invidious subscription sources independently of Jellyfin's guide task.</summary>
public sealed class InvidiousFeedRefreshTask : IScheduledTask
{
    private readonly ChannelService _channels;
    private readonly InvidiousFeedStore _store;
    private readonly IGuideManager _guide;

    /// <summary>Initializes the source refresh task.</summary>
    public InvidiousFeedRefreshTask(ChannelService channels, InvidiousFeedStore store, IGuideManager guide)
    {
        _channels = channels;
        _store = store;
        _guide = guide;
    }

    /// <inheritdoc />
    public string Name => "Refresh YouTube Sources";

    /// <inheritdoc />
    public string Key => "LiveChannelsRefreshInvidiousFeeds";

    /// <inheritdoc />
    public string Description => "Fetches Invidious subscription feeds, retains source videos for 72 hours, and rebuilds affected Live Channels schedules.";

    /// <inheritdoc />
    public string Category => "Live TV";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var token = Environment.GetEnvironmentVariable("LIVECHANNELS_INVIDIOUS_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("LIVECHANNELS_INVIDIOUS_TOKEN is not configured.");
        }

        var channels = _channels.GetEnabledChannels()
            .Where(channel => channel.Sources.Any(source => source.Kind == SourceKind.InvidiousFeed))
            .ToList();
        var sources = channels
            .SelectMany(channel => channel.Sources)
            .Where(source => source.Kind == SourceKind.InvidiousFeed && !string.IsNullOrWhiteSpace(source.InvidiousUrl))
            .GroupBy(source => source.InvidiousUrl.Trim().TrimEnd('/'), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(source => source.InvidiousMaximumResults).First())
            .ToList();

        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            var maximum = source.InvidiousMaximumResults <= 0 ? 50 : Math.Min(source.InvidiousMaximumResults, 200);
            await _store.RefreshAsync(source.InvidiousUrl, token, maximum, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
            progress.Report(sources.Count == 0 ? 50 : 70.0 * (index + 1) / sources.Count);
        }

        // Rebuild through Jellyfin's guide manager rather than updating only the plugin's playback cache.
        // Otherwise a newly inserted feed item changes the epoch-loop index used at tune-in while Jellyfin keeps
        // advertising the programme list from its previous guide refresh: the visible title is then commonly one
        // item behind the video that actually plays. GetProgramsAsync rebuilds the same cache as it publishes.
        progress.Report(75);
        await _guide.RefreshGuide(progress, cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        => new[]
        {
            new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger },
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(4).Ticks
            }
        };
}
