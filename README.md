# Jellyfin Plugin - Auto Generate Captions

Experimental Jellyfin plugin for Roku-driven, on-demand AI caption generation.

The plugin supports immediate rolling Live captions and an internal Full background pipeline used by Naqafin's Enhanced mode. Enhanced starts with Live captions, then switches to the completed speaker-labeled and polished VTT. It does not automatically scan the library or trigger work for Next Up items.

## Related Projects

- [Naqafin for Roku](https://github.com/naqadata/naqafin-roku): Roku client that exposes the `Auto-Generated` subtitle option and consumes this plugin's live WebVTT endpoint.
- [Naqafin Caption Worker](https://github.com/naqadata/naqafin-caption-worker): optional Dockerized CUDA worker that can run larger Whisper models on a separate GPU host.
- [Jellyfin Plugin Playlist Up Next](https://github.com/naqadata/jellyfin-plugin-playlist-up-next): separate companion server plugin used by Naqafin for playlist-aware Continue Watching and Next Up content.

## Client Support

This plugin is designed to work with [Naqafin for Roku](https://github.com/naqadata/naqafin-roku), an unofficial Roku client forked from the official Jellyfin Roku client.

Stock Jellyfin clients do not currently know how to start these caption sessions or poll the live generated WebVTT endpoint. Until equivalent support is accepted upstream or implemented by another client, Naqafin is the intended client for this plugin.

## Current State

The current implementation provides:

- Plugin configuration page.
- Capability endpoint so clients can gate optional generated-caption controls.
- Session start/status/stop endpoints.
- A live `.vtt` endpoint that returns valid WebVTT.
- Per-item cache clear endpoint.
- In-memory session state.
- First-chunk ffmpeg audio extraction.
- A bundled Python `stable_whisper` worker with backend/model logging.
- Resident local Whisper worker support to keep a model warm between chunk jobs when possible.
- Optional remote HTTP caption worker support for offloading transcription to a stronger GPU host.
- Parsing generated WebVTT cues back into the live endpoint.
- Persistent partial-chunk cache and stitched cache output for promotable models.
- Cue-shaping controls for max cue characters, max cue words, max cue duration, regrouping, and split-gap behavior.
- Rolling live transcription windows with a revisable tail and prior-transcript context.
- Read-only before/after context for OpenAI caption polishing.
- Explicit, low-priority full-item transcription through the remote worker.
- Optional local worker diarization with WebVTT speaker voice tags.
- Full-session status with real worker progress from 0-100%.
- Atomic promotion of completed Full output as a normal `AI Generated (Enhanced)` external subtitle track backed by durable plugin-managed storage.

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
  "hasCachedCaptions": false,
  "mode": "live",
  "progressPercent": 0,
  "isDiarized": false
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

Explicitly start a low-priority Full transcription:

```http
POST /AutoGenerateCaptions/Items/{itemId}/Full
Content-Type: application/json

{
  "mediaSourceId": null,
  "audioStreamIndex": -1,
  "language": "auto",
  "enableOpenAiPolish": true
}
```

Check server-advertised optional capabilities:

```http
GET /AutoGenerateCaptions/Capabilities
```

Clear generated-caption cache for an item:

```http
POST /AutoGenerateCaptions/Items/{itemId}/Cache/Clear
```

## Naqafin Integration Sketch

The matching Roku client is [Naqafin for Roku](https://github.com/naqadata/naqafin-roku).

In this workspace, the corresponding development checkout is usually at:

```text
../naqafin-roku
```

Client behavior:

1. Load the server plugin list and show generated-caption UI only when this plugin is available.
2. Add `AI Captions - Live` and, when advertised by capabilities, `AI Captions - Enhanced` entries to the subtitle menu.
3. For Enhanced, start Live and Full together, continue displaying Live while polling Full status, then switch to `enhancedVttUrl` when `enhancedReady` becomes true.
4. Set the custom caption task URL to the returned `liveVttUrl`.
5. Poll/reload VTT at `pollSeconds`, including current video `positionTicks`.
6. Let Subtitle Tools change generated-caption language and OpenAI polish settings, then restart the session when needed.
7. Poll Full session status to show `Live - Enhancing <progressPercent>%` in Subtitle Tools.
8. Call the stop endpoint when Live playback exits. Full jobs are background-owned and continue when playback pauses or exits.

## Worker Design

The worker should:

- Start ffmpeg at `positionTicks - 2s` where possible.
- Generate a small first chunk, then expand rolling windows while keeping a revisable tail.
- Keep `LookaheadSeconds` generated ahead of playback.
- Send earlier transcript text as Whisper context and reconcile repeated words at committed boundaries.
- Submit user-requested Full transcriptions at background priority in resumable worker slices.
- Diarize completed Full transcriptions locally on the worker when configured.
- Reconcile only tightly bounded unlabeled fragments, then polish Full captions in immutable speaker groups; a rejected group keeps its original cues without blocking later groups.
- Store generated ranges by `itemId + mediaSourceId + audioStreamIndex + language + model/config`.
- Keep chunk caches for all models, but only write stitched cache output when the model is listed in `Promotable models`.
- Atomically write successful Full output beneath Jellyfin's persistent data directory, register it immediately as an external `AI Generated (Enhanced)` subtitle track, and retain provenance alongside it.

Relevant cache/promotion settings:

- `Cache partial results`: keeps generated chunks so future sessions can reuse them.
- `Promote completed subtitles`: legacy setting retained for configuration compatibility; Enhanced Full output is promoted as part of the mode contract.
- `Promotable models`: comma-separated model allowlist for stitched cache and Enhanced output. Defaults to `large-v3, large-v3-turbo`; empty allows any model.

Relevant cue-shaping settings:

- `Max cue characters`: target maximum characters per generated cue before the worker splits or regroups text.
- `Max cue words`: target maximum words per generated cue.
- `Max cue duration seconds`: target maximum cue display duration.
- `Regroup split gap seconds`: pause length that encourages splitting speech into separate cues.
- `Polish generated captions with OpenAI`: optionally cleans buffered Live captions after enough lookahead exists and cleans Full captions after diarization. Caption generation does not require OpenAI, and failed Full groups retain their original cues.
- `OpenAI polish lookahead seconds`: minimum generated-caption buffer ahead of playback before polishing starts.
- `OpenAI polish window seconds`: maximum caption span sent to OpenAI in one pass.

These settings affect newly generated or regenerated caption chunks. Cached chunks are reused until the cache key changes or the cache is cleared.

## Remote Caption Worker

The plugin can optionally submit extracted audio chunks to a remote [Naqafin Caption Worker](https://github.com/naqadata/naqafin-caption-worker) before falling back to local Whisper.

The remote worker is not required. Without it, this plugin uses its local `stable_whisper` worker path.

Relevant plugin settings:

- `Use remote caption worker`: enables the remote-first path.
- `Remote worker URL`: worker base URL, for example `http://192.0.2.10:8765`.
- `Remote worker API key`: optional bearer token for protected workers.
- `Remote worker model`: model requested from the worker, for example `large-v3`.
- `Fallback to local when unavailable`: uses the local resident/per-job worker if the remote worker cannot be reached before a job starts.
- `Enable background prefetch`: enables explicit Full transcription requests. Despite the legacy configuration name, Naqafin does not automatically prefetch Next Up items.
- `Diarize background captions`: adds speaker labels to Full transcriptions and advertises the Full mode to Naqafin.

Remote jobs that start and then fail are treated as generation failures. That avoids silently restarting a long failed remote job on the weaker Jellyfin server.

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
- Cache file path/range writes and stitched cache eligibility decisions.

## Packaging

Create a new release package with an explicit version and changelog:

```bash
./scripts/package.sh 0.1.1 "Describe the release"
```

The script writes `dist/Jellyfin.Plugin.AutoGenerateCaptions_<version>.zip` and adds a matching `manifest.json` version entry with checksum and timestamp.

Release artifacts are treated as immutable once pushed. The script refuses to overwrite an existing zip or manifest version unless `--force` is passed, and it rejects versions lower than the latest manifest version.
