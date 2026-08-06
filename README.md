# Jellyfin LiveTube

This Jellyfin 10.11.x plugin provides looping virtual TV channels from your media library or YouTube (via  authenticated Invidious subscription channels), and an Android-compatible catch-up catalogue. It runs inside Jellyfin, so no external tuner service or client modification is required.

This repository is a maintained fork of [JPKribs/jellyfin-plugin-livechannels](https://github.com/JPKribs/jellyfin-plugin-livechannels). The upstream README is preserved as [README.upstream.md](README.upstream.md).

## Fork features

- Compound library filters: include or exclude genres and tags, match any or all values, and inherit series tags on episodes.
- Dynamic multi-source channels with rating, year, studio, person, language and content-type filters.
- Authenticated Invidious subscription feeds refreshed every four hours, with deduplicated source metadata retained for 72 hours.
- Original-audio Invidious playback with the best H.264 representation up to 1080p.
- Guide thumbnails cached locally; signed video and audio representations are resolved at playback time.
- Finite **Live Channels Catch-up VOD** items for the stock Jellyfin Android TV client.
- No duplicate media in catch-up: local programmes use their library files; Invidious media is range-proxied on demand. Only a short-lived control MPD is stored.
- QSV/VA-API and other Jellyfin-configured encoders, fixed-format channel output, HDR tone mapping, subtitle burn-in and session controls.

## Architecture

```text
Jellyfin library ─┐
                  ├─ ChannelService ─ schedule/guide ─ ILiveTvService ─ Android TV Live TV
Invidious feed ───┘                         │
                                           └─ IChannel catch-up catalogue
                                                ├─ local file (finite VOD)
                                                └─ short-lived MPD
                                                     └─ range proxy ─ Invidious media
```

Live TV remains a forward-only linear stream. `DirectLiveStream` serves a rolling MPEG-TS window through Jellyfin's own live-stream endpoint. Android TV correctly disables seeking for this infinite Live TV source.

Catch-up is a separate `IChannel` provider. It projects the last 24 hours of local schedule slots as finite ordinary videos and exposes each Invidious source video only once using its latest airing, which prevents short YouTube loops from producing duplicate Android TV cards. The stock client gains pause, seek, rewind, fast-forward and resume controls.

For Invidious catch-up, the plugin:

1. fetches a fresh DASH manifest;
2. selects one H.264 representation up to 1080p and the non-enhanced `Role=main` original audio;
3. rewrites the MPD to opaque local URLs;
4. range-proxies media bytes without writing them to disk;
5. removes control MPDs older than one hour.

The proxy tokens exist only in plugin memory and change on restart. Signed upstream URLs are never placed in the Jellyfin catalogue.

## Configuration

Open **Dashboard → Plugins → Live Channels**. The plugin has four tabs:

- **Channels** — channel identity, sources, filters, logos, ordering and import/export;
- **Popular** — built-in popularity channel;
- **Sessions** — active encoders and FFmpeg logs;
- **Settings** — output format, hardware acceleration, buffering, subtitles and limits.

[SETTINGS.md](SETTINGS.md) documents every option and valid range.

An Invidious source requires:

- an absolute instance URL;
- a read-only bearer token with `GET:feed` access;
- a maximum result count between 1 and 200 for each four-hour poll.

The **Refresh YouTube Sources** scheduled task also runs at startup. It merges entries by stable video ID and retains videos published within the preceding 72 hours; a temporary fetch failure leaves the prior retained set available. This store contains feed metadata only. Video/audio media remains just-in-time and is never retained.

Keep the token outside Git and plugin XML. Supply it to Jellyfin through a protected service environment variable or equivalent secret store.

## Build and test

Requirements: .NET 9 SDK and Jellyfin 10.11.x.

```bash
dotnet restore Jellyfin.Plugin.LiveChannels.sln
dotnet test Jellyfin.Plugin.LiveChannels.sln --configuration Release
```

The release assembly is:

```text
Jellyfin.Plugin.LiveChannels/bin/Release/net9.0/Jellyfin.Plugin.LiveChannels.dll
```

Install it in the plugin directory and restart Jellyfin. Preserve the previous DLL for rollback.

## Verification

After deployment:

1. Confirm the plugin is active.
2. Run **Refresh YouTube Sources**, then Jellyfin's **Refresh Guide** scheduled task.
3. Open Live TV and play a local and an Invidious channel.
4. Open **Live Channels Catch-up VOD** from My Media.
5. Verify a local item and an Invidious item seek in the stock Android TV client.
6. Confirm the catch-up cache contains MPD control files but no MP4, M4A or WebM copies.

The current fork targets Jellyfin 10.11.x (`net9.0`, ABI package 10.11.10). See [CATCHUP.md](CATCHUP.md) for the catch-up contract and limits.

## Licence and upstream notice

The project retains the upstream GPL-3.0 licence in [LICENSE](LICENSE). The upstream support and AI-generation notices remain in [README.upstream.md](README.upstream.md).
