# Catch-up and restart playback design

Jellyfin Android TV disables seeking for every Live TV session. The plugin therefore cannot add rewind to its existing `ILiveTvService` stream. Catch-up playback must use Jellyfin's ordinary video path.

## Proposed user model

Register a second Jellyfin provider named **Live Channels Catch-up** through `MediaBrowser.Controller.Channels.IChannel`.

The provider exposes:

- one folder per configured virtual channel;
- the current programme and a bounded set of past schedule slots in each folder;
- ordinary video items with stable IDs derived from the channel ID, slot start and source item ID;
- the guide title, episode metadata and cached artwork already stored in `ProgramEntry`.

Opening a catch-up item uses normal video playback. Android TV then permits pause, seek, rewind, fast-forward and resume because the item is not marked `IsLiveStream`.

## Local library programmes

A local schedule slot already stores the Jellyfin item ID, source path and runtime.

The catch-up item can expose the source file as a finite `MediaSourceInfo` with `RunTimeTicks` set. The first version should expose the whole programme. Selecting **Resume live position** can start the item at:

```text
UTC now - programme slot start
```

Selecting **Restart programme** starts at zero. Playback progress belongs to the catch-up item and does not alter the source library item's watched state unless this is made an explicit option.

No media copy is required. Existing access controls must still be applied to the channel item.

## Invidious programmes

An Invidious schedule slot stores the stable video ID, runtime, instance URL and cached thumbnail.

The provider cannot persist signed representation URLs because they expire. On first catch-up request it must:

1. Resolve the current H.264 representation and the non-enhanced `Role=main` original audio representation.
2. Download or remux them into a finite local media file under a bounded cache.
3. Write atomically and verify the output duration and streams before publishing it.
4. Return the cached file as an ordinary finite media source.

The cache key must include the video ID and selected output profile. Concurrent requests for one video must share one materialisation task. Failed partial files must not be published.

A later implementation can replace full materialisation with a seekable local proxy, but a finite file is compatible with stock Jellyfin clients and does not depend on expiring signed URLs during playback.

## Retention

Start with these limits:

- 24 hours of schedule history;
- 20 GB total Invidious media cache;
- least-recently-used eviction, excluding active files;
- no duplicate local-library media;
- cached thumbnails may outlive media files and are pruned separately.

## Jellyfin API surface

`IChannel` is suitable because `ChannelItemInfo` supports folders, finite video items, runtime, artwork and `MediaSourceInfo` entries. Jellyfin stores these items in its channel catalogue and plays non-live entries through the normal VOD pipeline.

The prototype must confirm these points against Jellyfin 10.11.10 and Android TV 0.19.9:

1. The **Live Channels Catch-up** provider is visible in the stock Android TV client.
2. A local item appears as a finite video and seeks correctly.
3. Resume position is stored per catch-up item.
4. A materialised Invidious item plays with original audio and seeks correctly.
5. Parent and user access restrictions are enforced.
6. Cache eviction cannot remove a file in active playback.

## Excluded approach

Making `SegmentConcatStream` seekable does not enable rewind. Jellyfin wraps `IDirectStreamProvider.GetStream()` in a `ProgressiveFileStream` whose `CanSeek` is false, normalises plugin Live TV sources to infinite streams, and Android TV returns false from `canSeek()` whenever `isLiveTv` is true.
