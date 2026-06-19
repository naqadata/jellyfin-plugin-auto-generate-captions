# Jellyfin Plugin Auto Generate Captions

Experimental Jellyfin plugin for Roku-driven, on-demand AI caption generation.

The goal is not to scan the whole library. A custom client starts a short-lived caption session when the viewer selects `Auto-Generated`, then polls a live WebVTT endpoint while the server generates and caches caption ranges.

## Related Projects

- [Naqafin for Roku](https://github.com/naqadata/naqafin-roku): Roku client that exposes the `Auto-Generated` subtitle option and consumes this plugin's live WebVTT endpoint.
- [Naqafin Caption Worker](https://github.com/naqadata/naqafin-caption-worker): optional Dockerized CUDA worker that can run larger Whisper models on a separate GPU host.
- [Jellyfin Plugin Playlist Up Next](https://github.com/naqadata/jellyfin-plugin-playlist-up-next): separate companion server plugin used by Naqafin for playlist-aware resume rows.

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
- Resident local Whisper worker support to keep a model warm between chunk jobs when possible.
- Optional remote HTTP caption worker support for offloading transcription to a stronger GPU host.
- Parsing generated WebVTT cues back into the live endpoint.
- Persistent partial-chunk cache and stitched cache output for promotable models.
- Cue-shaping controls for max cue characters, max cue words, max cue duration, regrouping, and split-gap behavior.

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
- Keep chunk caches for all models, but only write stitched cache output when the model is listed in `Promotable models`.
- External subtitle promotion is planned, but not implemented yet.

Relevant cache/promotion settings:

- `Cache partial results`: keeps generated chunks so future sessions can reuse them.
- `Promote completed subtitles`: reserved for future external subtitle promotion. Current builds write stitched cache VTTs for model-eligible sessions.
- `Promotable models`: comma-separated model allowlist for stitched cache output now and external subtitle promotion later. Defaults to `large-v3, large-v3-turbo`; empty allows any model.

Relevant cue-shaping settings:

- `Max cue characters`: target maximum characters per generated cue before the worker splits or regroups text.
- `Max cue words`: target maximum words per generated cue.
- `Max cue duration seconds`: target maximum cue display duration.
- `Regroup split gap seconds`: pause length that encourages splitting speech into separate cues.

These settings affect newly generated or regenerated caption chunks. Cached chunks are reused until the cache key changes or the cache is cleared.

## Remote Caption Worker

The plugin can optionally submit extracted audio chunks to a remote [Naqafin Caption Worker](https://github.com/naqadata/naqafin-caption-worker) before falling back to local Whisper.

The remote worker is not required. Without it, this plugin uses its local `stable_whisper` worker path.

Relevant plugin settings:

- `Use remote caption worker`: enables the remote-first path.
- `Remote worker URL`: worker base URL, for example `http://192.0.2.10:8765`.
- `Remote worker API key`: optional bearer token for protected workers.
- `Remote worker model`: model requested from the worker, for example `large-v3`.
- `Use local punctuation model`: asks the worker to restore punctuation locally after Whisper transcription.
- `Local punctuation model`: Hugging Face punctuation model requested from the worker. Defaults to `oliverguhr/fullstop-punctuation-multilang-large`.
- `Fallback to local when unavailable`: uses the local resident/per-job worker if the remote worker cannot be reached before a job starts.

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
