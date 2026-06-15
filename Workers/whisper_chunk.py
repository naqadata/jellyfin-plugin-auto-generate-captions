#!/usr/bin/env python3
import argparse
import gc
import json
import os
import sys
import time
import traceback
from datetime import timedelta

os.environ.setdefault("PYTORCH_CUDA_ALLOC_CONF", "expandable_segments:True")


def log(event, **fields):
    print(json.dumps({"event": event, **fields}), flush=True)


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


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--audio", required=True)
    parser.add_argument("--output", required=True)
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

        cuda_available = torch.cuda.is_available()
        gpu_name = torch.cuda.get_device_name(0) if cuda_available else ""
        gpu_memory_bytes = torch.cuda.get_device_properties(0).total_memory if cuda_available else 0
        log(
            "backend-probe",
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
                        model=candidate_model,
                        device=candidate_device,
                        reason=load_failures[-1]["errorType"],
                    )

                log("model-load-start", model=candidate_model, device=candidate_device)
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
                log("model-load-failed", **failure)
                clear_cuda(torch)

        if model is None:
            raise RuntimeError(f"all model load candidates failed: {load_failures!r}")

        log("model-load-complete", model=selected_model, device=selected_device, elapsedSeconds=time.monotonic() - load_started)

        transcribe_started = time.monotonic()
        language = None if args.language.lower() == "auto" else args.language
        try:
            result = model.transcribe(
                args.audio,
                verbose=False,
                language=language,
                vad=True,
                vad_threshold=args.vad_threshold,
            )
        except Exception as exc:
            log("transcribe-failed", model=selected_model, device=selected_device, errorType=type(exc).__name__, error=repr(exc))
            if selected_device != "cuda" or not args.allow_cpu_fallback:
                raise

            del model
            clear_cuda(torch)
            selected_model = args.fallback_model or args.model
            selected_device = "cpu"
            log("model-fallback", model=selected_model, device=selected_device, reason=type(exc).__name__)
            model = stable_whisper.load_model(selected_model, device=selected_device, in_memory=True)
            result = model.transcribe(
                args.audio,
                verbose=False,
                language=language,
                vad=True,
                vad_threshold=args.vad_threshold,
            )

        segments = list(getattr(result, "segments", []))
        transcribe_elapsed = time.monotonic() - transcribe_started
        log("transcribe-complete", segmentCount=len(segments), elapsedSeconds=transcribe_elapsed)

        write_vtt(args.output, segments, args.offset_seconds)
        log("vtt-written", output=args.output, elapsedSeconds=time.monotonic() - started)
        return 0
    except Exception as exc:
        log("worker-failed", error=repr(exc), traceback=traceback.format_exc())
        return 1


if __name__ == "__main__":
    sys.exit(main())
