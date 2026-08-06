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
public sealed class CatchupChannel : IChannel, IDisableMediaSourceDisplay, IRequiresMediaInfoCallback
{
    private static readonly TimeSpan History = TimeSpan.FromHours(24);
    private readonly ChannelService _channels;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly InvidiousCatchupManifest _invidiousManifest;

    /// <summary>Initializes the catch-up catalogue.</summary>
    public CatchupChannel(ChannelService channels, ILibraryManager libraryManager, IUserManager userManager, InvidiousCatchupManifest invidiousManifest)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(invidiousManifest);
        _channels = channels;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _invidiousManifest = invidiousManifest;
    }

    /// <inheritdoc />
    public string Name => "Live Channels Catch-up VOD";

    /// <inheritdoc />
    public string Description => "Restart and seek recent programmes from the virtual Live TV schedule.";

    /// <inheritdoc />
    public string DataVersion => "3";

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
            .Where(slot => slot.Start <= now && IsVisible(slot.Program, user))
            .OrderByDescending(slot => slot.Start)
            .Select(slot => BuildItem(channel.Id, slot))
            .ToList();
        return Task.FromResult(Result(items));
    }

    /// <inheritdoc />
    public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTime.UtcNow;
        foreach (var channel in _channels.GetEnabledChannels())
        {
            var programmes = _channels.ResolvePrograms(channel);
            var slot = _channels.BuildTimeline(channel, programmes, now - History, now.AddSeconds(1))
                .FirstOrDefault(candidate => string.Equals(ItemId(channel.Id, candidate.Start, candidate.Program.ItemId).ToString("N", CultureInfo.InvariantCulture), id, StringComparison.OrdinalIgnoreCase));
            if (slot is null)
            {
                continue;
            }

            if (!slot.Program.IsInvidious && !string.IsNullOrEmpty(slot.Program.Path) && File.Exists(slot.Program.Path))
            {
                return new[] { BuildMediaSource(channel.Id, slot, slot.Program.Path) };
            }

            if (slot.Program.IsInvidious && !string.IsNullOrEmpty(slot.Program.Path) && !string.IsNullOrEmpty(slot.Program.InvidiousUrl))
            {
                var manifest = await _invidiousManifest.GetAsync(slot.Program.InvidiousUrl, slot.Program.Path, cancellationToken).ConfigureAwait(false);
                return new[] { BuildInvidiousMediaSource(channel.Id, slot, manifest) };
            }
        }

        return Array.Empty<MediaSourceInfo>();
    }

    /// <inheritdoc />
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
        => Task.FromResult(new DynamicImageResponse { HasImage = false });

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedChannelImages() => Array.Empty<ImageType>();

    internal static ChannelItemInfo BuildItem(string channelId, ScheduledProgram slot)
    {
        var program = slot.Program;
        var sourceKey = program.Path ?? throw new ArgumentException("A catch-up programme needs a media identity.", nameof(slot));
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
            MediaSources = new List<MediaSourceInfo>(),
            Etag = Hash(string.Join('|', "v4", sourceKey, program.DurationTicks.ToString(CultureInfo.InvariantCulture), slot.Start.Ticks.ToString(CultureInfo.InvariantCulture)))
        };
    }

    internal static MediaSourceInfo BuildMediaSource(string channelId, ScheduledProgram slot, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var program = slot.Program;
        return new MediaSourceInfo
        {
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
    }

    internal static MediaSourceInfo BuildInvidiousMediaSource(string channelId, ScheduledProgram slot, InvidiousCatchupManifestResult manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var source = BuildMediaSource(channelId, slot, manifest.Path);
        var width = (int)Math.Round(manifest.VideoHeight * 16.0 / 9.0);
        // FFmpeg's DASH demuxer is named "dash"; advertising the .mpd extension as the container makes Jellyfin
        // force the nonexistent input format `-f mpd`, even though the control file itself is a valid MPD.
        source.Container = "dash";
        source.SupportsDirectPlay = false;
        source.SupportsDirectStream = false;
        source.SupportsProbing = false;
        source.MediaStreams = new List<MediaStream>
        {
            new()
            {
                Type = MediaStreamType.Video,
                Index = 0,
                Codec = "h264",
                Width = width,
                Height = manifest.VideoHeight,
                RealFrameRate = 30,
                AverageFrameRate = 30,
                IsInterlaced = false,
                PixelFormat = "yuv420p"
            },
            new()
            {
                Type = MediaStreamType.Audio,
                Index = 1,
                Codec = "aac",
                Channels = 2,
                SampleRate = 48000,
                Language = manifest.AudioLanguage,
                IsDefault = true
            }
        };
        return source;
    }

    private bool IsVisible(ProgramEntry program, Jellyfin.Database.Implementations.Entities.User user)
        => program.IsInvidious
            ? !string.IsNullOrEmpty(program.Path) && !string.IsNullOrEmpty(program.InvidiousUrl)
            : !string.IsNullOrEmpty(program.Path) && File.Exists(program.Path)
                && _libraryManager.GetItemById(program.ItemId)?.IsVisible(user) == true;

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
