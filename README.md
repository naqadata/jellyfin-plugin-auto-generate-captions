# Jellyfin Plugin Auto Generate Captions

Experimental Jellyfin plugin for Roku-driven, on-demand AI caption generation.

The goal is not to scan the whole library. A custom client starts a short-lived caption session when the viewer selects `Auto-Generated`, then polls a live WebVTT endpoint while the server generates and caches caption ranges.

## Client Support

This plugin is designed to work with [Naqafin for Roku](https://github.com/naqadata/naqafin-roku), an unofficial Roku client forked from the official Jellyfin Roku client.

Stock Jellyfin clients do not currently know how to start these caption sessions or poll the live generated WebVTT endpoint. Until equivalent support is accepted upstream or implemented by another client, Naqafin is the intended client for this plugin.

## Current State

This first implementation provides:

- Plugin configuration page.
- Session start/status/stop endpoints.
- A live `.vtt` endpoint that returns valid WebVTT.
- In-memory session state.
- First-chunk ffmpeg audio extraction.
- A bundled Python `stable_whisper` worker with backend/model logging.
- Parsing generated WebVTT cues back into the live endpoint.

## API Contract

Start a session:

```http
POST /AutoGenerateCaptions/Items/{itemId}/Sessions
Content-Type: application/json

{
  "playSessionId": "...",
  "mediaSourceId": "...",
  "audioStreamIndex": 1,
  "positionTicks": 1230000000,
  "language": "auto",
  "continueAfterPlaybackStops": false
}
```

Response:

```json
{
  "sessionId": "00000000-0000-0000-0000-000000000000",
  "itemId": "00000000-0000-0000-0000-000000000000",
  "mediaSourceId": "...",
  "audioStreamIndex": 1,
  "language": "auto",
  "status": "warming-up",
  "liveVttUrl": "/AutoGenerateCaptions/00000000-0000-0000-0000-000000000000/live.vtt",
  "pollSeconds": 2,
  "generatedThroughTicks": 1230000000,
  "hasCachedCaptions": false
}
```

Poll captions:

```http
GET /AutoGenerateCaptions/{sessionId}/live.vtt?positionTicks=1235000000
Accept: text/vtt
```

Status:

```http
GET /AutoGenerateCaptions/Sessions/{sessionId}
```

Stop:

```http
POST /AutoGenerateCaptions/Sessions/{sessionId}/Stop
```

## Naqafin Integration Sketch

The matching Roku client is [Naqafin for Roku](https://github.com/naqadata/naqafin-roku).

In this workspace, the corresponding development checkout is usually at:

```text
../naqafin-roku
```

Client behavior:

1. Add an `Auto-Generated` entry to the subtitle menu.
2. On selection, call `POST /AutoGenerateCaptions/Items/{itemId}/Sessions`.
3. Set the custom caption task URL to the returned `liveVttUrl`.
4. Poll/reload VTT at `pollSeconds`, including current video `positionTicks`.
5. Call the stop endpoint when playback exits or the user disables auto-generated captions.

## Worker Design

The worker should:

- Start ffmpeg at `positionTicks - 2s` where possible.
- Generate a small first chunk, then larger steady chunks.
- Keep `LookaheadSeconds` generated ahead of playback.
- Store generated ranges by `itemId + mediaSourceId + audioStreamIndex + language + model/config`.
- Promote completed captions to a normal external subtitle only when configured.

## Logging Requirements

The worker should log enough detail to debug startup and GPU behavior from Jellyfin logs:

- Requested backend and selected backend.
- GPU device name, VRAM, runtime/driver version when available.
- Whether inference is using GPU or CPU.
- Primary model path/name, fallback model path/name, quantization, and language.
- Model load duration and warmup duration.
- Fallback reason when primary model or GPU initialization fails.
- ffmpeg command shape, input seek position, selected audio stream, startup time, and extraction time.
- Per-chunk timings, realtime factor, generated range, and whether the chunk was served from cache.
- Cache file path/range writes and promotion to external subtitle files.

## Packaging

Create a new release package with an explicit version and changelog:

```bash
./scripts/package.sh 0.1.1 "Describe the release"
```

The script writes `dist/Jellyfin.Plugin.AutoGenerateCaptions_<version>.zip` and adds a matching `manifest.json` version entry with checksum and timestamp.

Release artifacts are treated as immutable once pushed. The script refuses to overwrite an existing zip or manifest version unless `--force` is passed, and it rejects versions lower than the latest manifest version.
