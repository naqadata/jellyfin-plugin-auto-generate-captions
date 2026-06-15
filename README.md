# Jellyfin Plugin Auto Generate Captions

Experimental Jellyfin plugin for Roku-driven, on-demand AI caption generation.

The goal is not to scan the whole library. A custom client starts a short-lived caption session when the viewer selects `Auto-Generated`, then polls a live WebVTT endpoint while the server generates and caches caption ranges.

## Current State

This first scaffold provides:

- Plugin configuration page.
- Session start/status/stop endpoints.
- A live `.vtt` endpoint that returns valid WebVTT.
- In-memory session state.
- Clear TODO boundary for the ffmpeg/Whisper worker.

It does not yet run ffmpeg or Whisper.

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

## Roku Integration Sketch

The matching Roku worktree is expected at:

```text
../jellyfin-roku-auto-generate-captions
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
