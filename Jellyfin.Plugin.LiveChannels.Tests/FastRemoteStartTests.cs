using System.Collections.Generic;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Services;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>Protects the one-segment fast handover from leaking into local-library channels.</summary>
public class FastRemoteStartTests
{
    [Fact]
    public void PureInvidiousChannel_UsesFastRemoteStart()
    {
        var channel = new Channel
        {
            Sources = new List<LibrarySource> { new() { Kind = SourceKind.InvidiousFeed } }
        };

        Assert.True(LiveChannelsTvService.FastRemoteStart(channel));
    }

    [Fact]
    public void LocalOrMixedChannel_KeepsFullConfiguredBuffer()
    {
        var local = new Channel
        {
            Sources = new List<LibrarySource> { new() { Kind = SourceKind.Library } }
        };
        var mixed = new Channel
        {
            Sources = new List<LibrarySource>
            {
                new() { Kind = SourceKind.InvidiousFeed },
                new() { Kind = SourceKind.Library }
            }
        };

        Assert.False(LiveChannelsTvService.FastRemoteStart(local));
        Assert.False(LiveChannelsTvService.FastRemoteStart(mixed));
        Assert.False(LiveChannelsTvService.FastRemoteStart(new Channel()));
    }
}
