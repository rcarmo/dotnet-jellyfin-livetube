using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LiveChannels.Models;

/// <summary>
/// One video returned by an authenticated Invidious subscription feed. This is deliberately independent of
/// Jellyfin library items: the stable video id is retained and playback media is resolved only when the item airs.
/// </summary>
public sealed class InvidiousFeedVideo
{
    /// <summary>Gets or sets the stable YouTube video id.</summary>
    public string VideoId { get; set; } = string.Empty;

    /// <summary>Gets or sets the video title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the publishing channel name.</summary>
    public string Author { get; set; } = string.Empty;

    /// <summary>Gets or sets the publishing channel id.</summary>
    public string AuthorId { get; set; } = string.Empty;

    /// <summary>Gets or sets the Unix publication timestamp.</summary>
    public long Published { get; set; }

    /// <summary>Gets or sets the duration in seconds.</summary>
    public long LengthSeconds { get; set; }

    /// <summary>Gets or sets the available thumbnail variants.</summary>
    public List<InvidiousThumbnail> VideoThumbnails { get; set; } = new();

    /// <summary>Gets the publication time as UTC, or <c>null</c> for an invalid timestamp.</summary>
    public DateTime? PublishedUtc
    {
        get
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(Published).UtcDateTime;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
    }
}

/// <summary>One image variant returned with an Invidious feed video.</summary>
public sealed class InvidiousThumbnail
{
    /// <summary>Gets or sets the thumbnail quality label.</summary>
    public string Quality { get; set; } = string.Empty;

    /// <summary>Gets or sets the thumbnail URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the image width.</summary>
    public int Width { get; set; }

    /// <summary>Gets or sets the image height.</summary>
    public int Height { get; set; }
}
