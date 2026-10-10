#!/usr/bin/env python3
"""Run the source-built Avalonia harness, retaining precise per-mode evidence.

This does not execute the published oChan apphost and is not an artifact smoke.
Linux uses Xvfb with no tray service. Native Windows/macOS require a GUI session;
a missing graphical session fails the check, so it cannot silently count as pass.
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import tempfile


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--assembly", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--timeout", type=int, default=60)
    args = parser.parse_args()
    assembly = Path(args.assembly).resolve(strict=True)
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    if sys.platform.startswith("linux") and shutil.which("xvfb-run") is None:
        raise RuntimeError("xvfb-run is required for the Linux graphical smoke.")
    modes = ("normal", "locked-profile", "no-tray", "tray-failure")
    results: list[dict[str, object]] = []
    failed = False
    for mode in modes:
        destination = output / mode
        destination.mkdir(exist_ok=True)
        command = ["dotnet", str(assembly)]
        if mode != "normal":
            command.append("--" + mode)
        if sys.platform.startswith("linux"):
            command = ["xvfb-run", "--auto-servernum", "--server-args=-screen 0 1280x1024x24", *command]
        previous = {image: image.stat().st_mtime_ns for image in assembly.parent.glob("smoke-*.png")}
        timed_out = False
        with tempfile.TemporaryDirectory(prefix="ochan-smoke-cwd-") as working_directory:
            with (destination / "output.log").open("w", encoding="utf-8") as log:
                process = subprocess.Popen(command, cwd=working_directory, stdout=log,
                                           stderr=subprocess.STDOUT, start_new_session=os.name != "nt")
                try:
                    exit_code = process.wait(timeout=args.timeout)
                except subprocess.TimeoutExpired:
                    timed_out = True
                    if os.name == "nt":
                        subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], check=False,
                                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                    else:
                        try:
                            os.killpg(process.pid, signal.SIGKILL)
                        except ProcessLookupError:
                            pass
                    process.wait(timeout=10)
                    exit_code = 124
        log_text = (destination / "output.log").read_text(encoding="utf-8", errors="replace")
        print(f"--- Desktop harness {mode}: exit {exit_code} ---", flush=True)
        print(log_text, flush=True)
        for image in assembly.parent.glob("smoke-*.png"):
            if previous.get(image) != image.stat().st_mtime_ns:
                shutil.copy2(image, destination / image.name)
        passed = exit_code == 0 and "SMOKE PASS:" in log_text
        results.append({"mode": mode, "exit_code": exit_code, "timed_out": timed_out, "passed": passed})
        failed = failed or not passed
    (output / "results.json").write_text(json.dumps({
        "kind": "source-built Avalonia harness; not published apphost",
        "platform": sys.platform, "results": results
    }, indent=2) + "\n", encoding="utf-8")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
