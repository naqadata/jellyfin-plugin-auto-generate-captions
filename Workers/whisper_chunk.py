#!/usr/bin/env python3
import argparse
import gc
import json
import os
import sys
import time
import traceback
from datetime import timedelta
import wave

os.environ.setdefault("PYTORCH_CUDA_ALLOC_CONF", "expandable_segments:True")


def log(event, stream=None, **fields):
    print(json.dumps({"event": event, **fields}), file=stream or sys.stdout, flush=True)


def format_timestamp(seconds):
    seconds = max(0.0, float(seconds))
    td = timedelta(seconds=seconds)
    total_seconds = td.total_seconds()
    hours, remainder = divmod(int(total_seconds), 3600)
    minutes, whole_seconds = divmod(remainder, 60)
    milliseconds = int((total_seconds - int(total_seconds)) * 1000)
    return f"{hours:02}:{minutes:02}:{whole_seconds:02}.{milliseconds:03}"


def write_vtt(path, segments, offset_seconds):
    with open(path, "w", encoding="utf-8") as output:
        output.write("WEBVTT\n\n")
        for index, segment in enumerate(segments):
            start = offset_seconds + float(segment.start)
            end = offset_seconds + float(segment.end)
            text = segment.text.strip()
            if not text:
                continue

            output.write(f"chunk-{index}\n")
            output.write(f"{format_timestamp(start)} --> {format_timestamp(end)}\n")
            output.write(text)
            output.write("\n\n")


def clear_cuda(torch):
    gc.collect()
    if torch.cuda.is_available():
        torch.cuda.empty_cache()
        try:
            torch.cuda.ipc_collect()
        except Exception:
            pass


def load_model(args, torch, stable_whisper, log_stream=None):
    cuda_available = torch.cuda.is_available()
    gpu_name = torch.cuda.get_device_name(0) if cuda_available else ""
    gpu_memory_bytes = torch.cuda.get_device_properties(0).total_memory if cuda_available else 0
    log(
        "backend-probe",
        stream=log_stream,
        requestedBackend=args.backend,
        cudaAvailable=cuda_available,
        gpuName=gpu_name,
        gpuMemoryBytes=gpu_memory_bytes,
        torchVersion=getattr(torch, "__version__", ""),
    )

    selected_model = args.model
    selected_device = "cpu"
    if args.backend.lower() != "cpu" and cuda_available:
        selected_device = "cuda"

    load_started = time.monotonic()
    load_failures = []
    candidates = [(selected_model, selected_device)]
    if selected_device == "cuda" and args.fallback_model and args.fallback_model != selected_model:
        candidates.append((args.fallback_model, "cuda"))

    cpu_fallback_model = args.fallback_model or args.model
    if args.allow_cpu_fallback:
        candidates.append((cpu_fallback_model, "cpu"))

    model = None
    for candidate_model, candidate_device in candidates:
        try:
            if load_failures:
                log(
                    "model-fallback",
                    stream=log_stream,
                    model=candidate_model,
                    device=candidate_device,
                    reason=load_failures[-1]["errorType"],
                )

            log("model-load-start", stream=log_stream, model=candidate_model, device=candidate_device)
            model = stable_whisper.load_model(candidate_model, device=candidate_device, in_memory=True)
            selected_model = candidate_model
            selected_device = candidate_device
            break
        except Exception as exc:
            failure = {
                "model": candidate_model,
                "device": candidate_device,
                "errorType": type(exc).__name__,
                "error": repr(exc),
            }
            load_failures.append(failure)
            log("model-load-failed", stream=log_stream, **failure)
            clear_cuda(torch)

    if model is None:
        raise RuntimeError(f"all model load candidates failed: {load_failures!r}")

    log("model-load-complete", stream=log_stream, model=selected_model, device=selected_device, elapsedSeconds=time.monotonic() - load_started)
    return model, selected_model, selected_device


def transcribe_audio(args, model, selected_model, selected_device, torch, stable_whisper, audio, output, offset_seconds, language_value, log_stream=None):
    transcribe_started = time.monotonic()
    language = None if language_value.lower() == "auto" else language_value
    try:
        result = model.transcribe(
            audio,
            verbose=False,
            language=language,
            vad=True,
            vad_threshold=args.vad_threshold,
        )
    except Exception as exc:
        log("transcribe-failed", stream=log_stream, model=selected_model, device=selected_device, errorType=type(exc).__name__, error=repr(exc))
        if selected_device != "cuda" or not args.allow_cpu_fallback:
            raise

        del model
        clear_cuda(torch)
        selected_model = args.fallback_model or args.model
        selected_device = "cpu"
        log("model-fallback", stream=log_stream, model=selected_model, device=selected_device, reason=type(exc).__name__)
        model = stable_whisper.load_model(selected_model, device=selected_device, in_memory=True)
        result = model.transcribe(
            audio,
            verbose=False,
            language=language,
            vad=True,
            vad_threshold=args.vad_threshold,
        )

    segments = list(getattr(result, "segments", []))
    transcribe_elapsed = time.monotonic() - transcribe_started
    log("transcribe-complete", stream=log_stream, segmentCount=len(segments), elapsedSeconds=transcribe_elapsed)

    write_vtt(output, segments, offset_seconds)
    return model, selected_model, selected_device, len(segments)


def write_silence_wav(path):
    with wave.open(path, "wb") as output:
        output.setnchannels(1)
        output.setsampwidth(2)
        output.setframerate(16000)
        output.writeframes(b"\x00\x00" * 16000)


def run_server(args):
    import tempfile
    import torch
    import whisper
    import stable_whisper

    started = time.monotonic()
    model, selected_model, selected_device = load_model(args, torch, stable_whisper, log_stream=sys.stderr)

    warmup_started = time.monotonic()
    with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as temp:
        warmup_audio = temp.name
    warmup_output = warmup_audio + ".vtt"
    try:
        write_silence_wav(warmup_audio)
        model, selected_model, selected_device, _ = transcribe_audio(
            args,
            model,
            selected_model,
            selected_device,
            torch,
            stable_whisper,
            warmup_audio,
            warmup_output,
            0.0,
            "auto",
            log_stream=sys.stderr,
        )
    finally:
        for path in [warmup_audio, warmup_output]:
            try:
                os.remove(path)
            except OSError:
                pass

    log(
        "worker-ready",
        stream=sys.stderr,
        model=selected_model,
        device=selected_device,
        warmupSeconds=time.monotonic() - warmup_started,
        startupSeconds=time.monotonic() - started,
    )

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue

        request_started = time.monotonic()
        try:
            request = json.loads(line)
            request_id = request["id"]
            if request.get("action") == "shutdown":
                print(json.dumps({"id": request_id, "ok": True, "shutdown": True}), flush=True)
                return 0

            model, selected_model, selected_device, segment_count = transcribe_audio(
                args,
                model,
                selected_model,
                selected_device,
                torch,
                stable_whisper,
                request["audio"],
                request["output"],
                float(request.get("offsetSeconds", 0.0)),
                request.get("language", args.language),
                log_stream=sys.stderr,
            )
            log("vtt-written", stream=sys.stderr, output=request["output"], elapsedSeconds=time.monotonic() - request_started)
            print(json.dumps({
                "id": request_id,
                "ok": True,
                "segmentCount": segment_count,
                "model": selected_model,
                "device": selected_device,
                "elapsedSeconds": time.monotonic() - request_started,
            }), flush=True)
        except Exception as exc:
            request_id = "unknown"
            try:
                request_id = request.get("id", request_id)
            except Exception:
                pass

            log("worker-request-failed", stream=sys.stderr, error=repr(exc), traceback=traceback.format_exc())
            print(json.dumps({"id": request_id, "ok": False, "error": repr(exc)}), flush=True)

    return 0


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--server", action="store_true")
    parser.add_argument("--audio")
    parser.add_argument("--output")
    parser.add_argument("--model", required=True)
    parser.add_argument("--fallback-model", default="")
    parser.add_argument("--language", default="auto")
    parser.add_argument("--backend", default="cuda")
    parser.add_argument("--allow-cpu-fallback", action="store_true")
    parser.add_argument("--offset-seconds", type=float, default=0.0)
    parser.add_argument("--vad-threshold", type=float, default=0.35)
    args = parser.parse_args()

    started = time.monotonic()
    try:
        import torch
        import whisper
        import stable_whisper

        if args.server:
            return run_server(args)

        if not args.audio or not args.output:
            raise ValueError("--audio and --output are required unless --server is used")

        model, selected_model, selected_device = load_model(args, torch, stable_whisper)
        transcribe_audio(
            args,
            model,
            selected_model,
            selected_device,
            torch,
            stable_whisper,
            args.audio,
            args.output,
            args.offset_seconds,
            args.language,
        )
        log("vtt-written", output=args.output, elapsedSeconds=time.monotonic() - started)
        return 0
    except Exception as exc:
        log("worker-failed", error=repr(exc), traceback=traceback.format_exc())
        return 1


if __name__ == "__main__":
    sys.exit(main())
