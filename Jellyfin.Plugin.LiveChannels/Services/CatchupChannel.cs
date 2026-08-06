using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveChannels.Models;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>
/// Exposes recent virtual-channel programmes as finite video items. Jellyfin clients play these through the normal
/// VOD path, so pause, seek, rewind, fast-forward and resume work without changing the Live TV client.
/// </summary>
public sealed class CatchupChannel : IChannel, IDisableMediaSourceDisplay
{
    private static readonly TimeSpan History = TimeSpan.FromHours(24);
    private readonly ChannelService _channels;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    /// <summary>Initializes the catch-up catalogue.</summary>
    public CatchupChannel(ChannelService channels, ILibraryManager libraryManager, IUserManager userManager)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(userManager);
        _channels = channels;
        _libraryManager = libraryManager;
        _userManager = userManager;
    }

    /// <inheritdoc />
    public string Name => "Live Channels Catch-up";

    /// <inheritdoc />
    public string Description => "Restart and seek recent programmes from the virtual Live TV schedule.";

    /// <inheritdoc />
    public string DataVersion => "2";

    /// <inheritdoc />
    public string HomePageUrl => string.Empty;

    /// <inheritdoc />
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    /// <inheritdoc />
    public InternalChannelFeatures GetChannelFeatures()
        => new()
        {
            MediaTypes = new List<ChannelMediaType> { ChannelMediaType.Video },
            ContentTypes = new List<ChannelMediaContentType> { ChannelMediaContentType.TvExtra },
            DefaultSortFields = new List<ChannelItemSortField> { ChannelItemSortField.DateCreated },
            SupportsSortOrderToggle = false,
            SupportsContentDownloading = false,
            AutoRefreshLevels = 2
        };

    /// <inheritdoc />
    public bool IsEnabledFor(string userId) => true;

    /// <inheritdoc />
    public Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(query.FolderId))
        {
            var folders = _channels.GetEnabledChannels()
                .Select(channel => new ChannelItemInfo
                {
                    Id = FolderId(channel.Id).ToString("N", CultureInfo.InvariantCulture),
                    Name = channel.Number.ToString(CultureInfo.InvariantCulture) + " · " + channel.Name,
                    Type = ChannelItemType.Folder,
                    FolderType = ChannelFolderType.Container,
                    MediaType = ChannelMediaType.Video,
                    DateModified = DateTime.UtcNow
                })
                .ToList();
            return Task.FromResult(Result(folders));
        }

        var channel = _channels.GetEnabledChannels().FirstOrDefault(candidate =>
            string.Equals(FolderId(candidate.Id).ToString("N", CultureInfo.InvariantCulture), query.FolderId, StringComparison.OrdinalIgnoreCase));
        if (channel is null)
        {
            return Task.FromResult(Result(Array.Empty<ChannelItemInfo>()));
        }

        var user = query.UserId == Guid.Empty ? null : _userManager.GetUserById(query.UserId);
        if (user is null)
        {
            return Task.FromResult(Result(Array.Empty<ChannelItemInfo>()));
        }

        var now = DateTime.UtcNow;
        var programmes = _channels.ResolvePrograms(channel);
        var timeline = _channels.BuildTimeline(channel, programmes, now - History, now.AddSeconds(1));
        var items = timeline
            .Where(slot => slot.Start <= now
                && !slot.Program.IsInvidious
                && !string.IsNullOrEmpty(slot.Program.Path)
                && File.Exists(slot.Program.Path)
                && _libraryManager.GetItemById(slot.Program.ItemId)?.IsVisible(user) == true)
            .OrderByDescending(slot => slot.Start)
            .Select(slot => BuildLocalItem(channel.Id, slot))
            .ToList();
        return Task.FromResult(Result(items));
    }

    /// <inheritdoc />
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
        => Task.FromResult(new DynamicImageResponse { HasImage = false });

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedChannelImages() => Array.Empty<ImageType>();

    internal static ChannelItemInfo BuildLocalItem(string channelId, ScheduledProgram slot)
    {
        var program = slot.Program;
        var path = program.Path ?? throw new ArgumentException("A local catch-up programme needs a media path.", nameof(slot));
        var source = new MediaSourceInfo
        {
            // Jellyfin's HLS controller parses MediaSourceId as a GUID for channel items.
            Id = ItemId(channelId, slot.Start, program.ItemId).ToString("N", CultureInfo.InvariantCulture),
            Name = program.Title,
            Path = path,
            Protocol = MediaProtocol.File,
            RunTimeTicks = program.DurationTicks,
            IsInfiniteStream = false,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsProbing = true,
            Container = Path.GetExtension(path).TrimStart('.')
        };

        return new ChannelItemInfo
        {
            Id = ItemId(channelId, slot.Start, program.ItemId).ToString("N", CultureInfo.InvariantCulture),
            Name = program.Title,
            SeriesName = program.SeriesName,
            Overview = program.Overview,
            Type = ChannelItemType.Media,
            MediaType = ChannelMediaType.Video,
            ContentType = program.IsMovie ? ChannelMediaContentType.Movie : ChannelMediaContentType.Episode,
            IsLiveStream = false,
            RunTimeTicks = program.DurationTicks,
            DateCreated = slot.Start,
            StartDate = slot.Start,
            EndDate = slot.Stop,
            PremiereDate = program.PremiereDate,
            ProductionYear = program.Year,
            IndexNumber = program.EpisodeNumber,
            ParentIndexNumber = program.SeasonNumber,
            OfficialRating = program.OfficialRating,
            CommunityRating = program.CommunityRating,
            Genres = program.Genres.ToList(),
            ImageUrl = program.ThumbImagePath ?? program.PrimaryImagePath,
            MediaSources = new List<MediaSourceInfo> { source },
            Etag = Hash(string.Join('|', "v2", path, program.DurationTicks.ToString(CultureInfo.InvariantCulture), slot.Start.Ticks.ToString(CultureInfo.InvariantCulture)))
        };
    }

    internal static Guid FolderId(string channelId) => StableId("catchup-folder:" + channelId);

    internal static Guid ItemId(string channelId, DateTime start, Guid itemId)
        => StableId(string.Create(CultureInfo.InvariantCulture, $"catchup-item-v2:{channelId}:{start.Ticks}:{itemId:N}"));

    private static ChannelItemResult Result(IEnumerable<ChannelItemInfo> items)
    {
        var list = items.ToList();
        return new ChannelItemResult { Items = list, TotalRecordCount = list.Count };
    }

    private static Guid StableId(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
