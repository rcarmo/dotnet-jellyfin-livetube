# Catch-up and restart playback

The plugin exposes recent schedule slots as finite Jellyfin video items. Stock Jellyfin Android TV can seek these items without client changes.

## User model

The `IChannel` provider is named **Live Channels Catch-up VOD**. It exposes:

- one folder per enabled virtual channel;
- the current programme and completed schedule slots from the last 24 hours;
- stable item IDs derived from channel ID, slot start and source item ID;
- guide metadata and cached artwork from `ProgramEntry`;
- finite `MediaSourceInfo` records resolved at playback time through `IRequiresMediaInfoCallback`.

Catch-up progress belongs to its projected channel item. Live TV remains an infinite, forward-only stream and does not gain rewind controls.

## Local programmes

Local items use the original indexed file. The callback supplies the real path and finite runtime. Jellyfin enforces the requesting user's normal library visibility before the item appears.

No duplicate file is created.

## Invidious programmes

Signed Invidious URLs expire and cannot be persisted in Jellyfin's channel catalogue. On each playback request the plugin:

1. fetches a fresh Invidious DASH manifest;
2. identifies the non-enhanced `Role=main` original audio adaptation set;
3. selects the highest H.264 video representation at or below 1080p;
4. removes all other audio and video representations;
5. writes a short-lived MPD whose `BaseURL` entries use opaque plugin URLs;
6. publishes the MPD over Jellyfin's local HTTP endpoint;
7. range-proxies each selected remote representation to FFmpeg.

The range proxy streams response bodies directly. It does not save video or audio payloads. FFmpeg receives exactly two streams—video index 0 and original audio index 1—and Jellyfin packages them for the requesting client's normal VOD path.

## Retention and bounds

- Schedule history: 24 hours.
- MPD freshness before regeneration: four minutes.
- MPD cleanup age: one hour.
- Maximum MPD size: 2 MiB.
- Media payload cache: none.
- Concurrent requests for one video ID share one manifest-generation gate.
- Publication and media tokens are random 128-bit values held in memory and disappear on restart.
- Invidious guide artwork uses the separate bounded artwork cache.

## Verified contract

Verified on 6 August 2026 with Jellyfin 10.11.11 and the Android TV 0.19.9 device profile:

- the provider appears in Jellyfin's user views;
- a local source returns a finite runtime and valid Android-profile HLS output;
- an Invidious source returns `IsInfiniteStream: false` and finite runtime;
- the selected streams are H.264 video plus `Role=main` original AAC audio;
- a generated three-second HLS segment decodes with both video and audio;
- the catch-up directory contains only the control MPD, with no MP4, M4A or WebM copies.

## Live TV limitation

Making `SegmentConcatStream` seekable would not enable Android TV rewind. Jellyfin presents plugin Live TV sources as infinite and wraps direct providers in a non-seekable progressive stream. Catch-up therefore uses the ordinary finite-video contract; Live TV retains channel surfing and wall-clock schedule semantics.
