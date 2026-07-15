# Jellyfin Plugin - Auto Generate Captions

Experimental Jellyfin plugin for Roku-driven, on-demand AI caption generation.

The plugin supports immediate rolling Live captions plus a server-owned Enhanced queue for durable, full-item subtitles. The queue is administered from the plugin dashboard; it does not scan the library automatically, depend on Naqafin, or trigger work for Next Up items.

## Related Projects

- [Naqafin for Roku](https://github.com/naqadata/naqafin-roku): Roku client that exposes the `Auto-Generated` subtitle option and consumes this plugin's live WebVTT endpoint.
- [Naqafin Caption Worker](https://github.com/naqadata/naqafin-caption-worker): optional Dockerized CUDA worker that can run larger Whisper models on a separate GPU host.
- [Jellyfin Plugin Playlist Up Next](https://github.com/naqadata/jellyfin-plugin-playlist-up-next): separate companion server plugin used by Naqafin for playlist-aware Continue Watching and Next Up content.

## Client Support

This plugin is designed to work with [Naqafin for Roku](https://github.com/naqadata/naqafin-roku), an unofficial Roku client forked from the official Jellyfin Roku client.

Stock Jellyfin clients do not currently know how to start these caption sessions or poll the live generated WebVTT endpoint. Until equivalent support is accepted upstream or implemented by another client, Naqafin is the intended client for this plugin.

## Current State

The current implementation provides:

- Plugin administration page with separate Settings and Processing tabs.
- Read-only active/recent processing view with plugin phase, worker queue state, progress, and failure details.
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
- Atomic promotion of completed Full output to a Plex-compatible `.eng.vtt` sidecar beside the media file.
- Durable `diarization-turns.json` diagnostics for Full jobs and an explicit polishing phase for clients.

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

List active and recent work for an elevated administrator:

```http
GET /AutoGenerateCaptions/Admin/Jobs?limit=50
```

The Processing tab polls this endpoint every three seconds. Its recent history is intentionally in-memory and resets when Jellyfin restarts.

Search and queue server-owned Enhanced work for an elevated administrator:

```http
GET  /AutoGenerateCaptions/Admin/Search?query=Foundation
POST /AutoGenerateCaptions/Admin/Queue/{itemId}?overwriteExistingSubtitle=false
GET  /AutoGenerateCaptions/Admin/Queue?limit=100
```

Movies and episodes queue directly; seasons and series expand to their video children. Queue state is persisted below the plugin data directory and an interrupted running job is requeued after Jellyfin restarts. Set `overwriteExistingSubtitle=true` only when intentionally replacing an existing sidecar; it also bypasses completed cache output and retranscribes the item.

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
2. Add `AI Captions - Live` to the subtitle menu.
3. Set the custom caption task URL to the returned `liveVttUrl`.
4. Poll/reload VTT at `pollSeconds`, including current video `positionTicks`.
5. Let Subtitle Tools change generated-caption language and OpenAI polish settings, then restart the session when needed.
6. Call the stop endpoint when Live playback exits.

## Worker Design

The worker should:

- Start ffmpeg at `positionTicks - 2s` where possible.
- Generate a small first chunk, then expand rolling windows while keeping a revisable tail.
- Keep `LookaheadSeconds` generated ahead of playback.
- Send earlier transcript text as Whisper context and reconcile repeated words at committed boundaries.
- Submit administrator-queued Full transcriptions at background priority in resumable worker slices.
- Diarize completed Full transcriptions locally on the worker when configured.
- Reconcile only tightly bounded unlabeled fragments, then polish Full captions in immutable speaker groups; a rejected group keeps its original cues without blocking later groups.
- Store generated ranges by `itemId + mediaSourceId + audioStreamIndex + language + model/config`.
- Keep chunk caches for all models, but only write stitched cache output when the model is listed in `Promotable models`.
- Atomically write successful Full output beside its media as `<media-base>.eng.vtt`, register it as an external `AI Generated (Enhanced)` track, and retain provenance in the plugin cache. Existing sidecars are protected unless the queue request explicitly enables replacement.

Relevant cache/promotion settings:

- `Cache partial results`: keeps generated chunks so future sessions can reuse them.
- `Promote completed subtitles`: legacy setting retained for configuration compatibility; Enhanced Full output is promoted as part of the mode contract.
- `Promotable models`: comma-separated model allowlist for stitched cache and Enhanced output. Defaults to `large-v3, large-v3-turbo`; empty allows any model.

Relevant cue-shaping settings:

- `Max cue characters`: target maximum characters per generated cue before the worker splits or regroups text.
- `Max cue words`: target maximum words per generated cue.
- `Max cue duration seconds`: target maximum cue display duration.
- `Regroup split gap seconds`: pause length that encourages splitting speech into separate cues.
- `Polish generated captions`: optionally cleans buffered Live captions after enough lookahead exists and cleans Full captions after diarization. Caption generation does not require a cloud API, and failed groups retain their original cues.
- `Caption polish provider`: choose `OpenAI`, `Local`, or `Disabled`. Existing installations remain on OpenAI until changed.
- `Local polish URL`: an OpenAI-compatible `/v1/chat/completions` endpoint, such as Ollama. The plugin requests strict JSON-schema output and validates it before replacing any cue.
- `Local Live polish model`: a compact model intended to share GPU memory with Whisper.
- `Local Full polish model`: a larger model used by server-side Enhanced jobs only after other plugin transcription has become idle.
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
- `Enable server-side Enhanced queue`: enables administrator-queued Full transcription. It never automatically prefetches Next Up items.
- `Diarize background captions`: adds speaker labels to Full transcriptions.

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
