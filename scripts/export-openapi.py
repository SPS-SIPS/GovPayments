#!/usr/bin/env python3
import json
import os
import re
import signal
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "src" / "Sps.P2G.Gateway" / "Sps.P2G.Gateway.csproj"
PORT = int(os.environ.get("OPENAPI_PORT", "5088"))
OUT_DIR = ROOT / os.environ.get("OPENAPI_OUT_DIR", "artifacts/openapi")
BASE_URL = f"http://127.0.0.1:{PORT}"
OPENAPI_URL = f"{BASE_URL}/openapi/v1.json"
TIMEOUT_SECONDS = int(os.environ.get("OPENAPI_TIMEOUT_SECONDS", "45"))


def main() -> int:
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    log_path = OUT_DIR / "gateway.log"
    env = os.environ.copy()
    env.update(
        {
            "ASPNETCORE_ENVIRONMENT": env.get("ASPNETCORE_ENVIRONMENT", "Development"),
            "ASPNETCORE_URLS": BASE_URL,
            "Callbacks__Enabled": "false",
            "Database__RunMigrationsOnStartup": "false",
        }
    )

    with log_path.open("w", encoding="utf-8") as log_file:
        process = subprocess.Popen(
            ["dotnet", "run", "--no-build", "--no-launch-profile", "--project", str(PROJECT)],
            cwd=ROOT,
            env=env,
            stdout=log_file,
            stderr=subprocess.STDOUT,
            text=True,
        )

        try:
            document = wait_for_openapi(process, log_path)
            json_path = OUT_DIR / "openapi.json"
            yaml_path = OUT_DIR / "openapi.yaml"
            json_path.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
            yaml_path.write_text(to_yaml(document), encoding="utf-8")
            print(f"Wrote {json_path.relative_to(ROOT)}")
            print(f"Wrote {yaml_path.relative_to(ROOT)}")
            print(f"Gateway log: {log_path.relative_to(ROOT)}")
            return 0
        finally:
            stop_process(process)


def wait_for_openapi(process: subprocess.Popen[str], log_path: Path) -> object:
    deadline = time.monotonic() + TIMEOUT_SECONDS
    last_error = ""
    while time.monotonic() < deadline:
        if process.poll() is not None:
            output = read_log(log_path)
            raise RuntimeError(f"Gateway exited before OpenAPI was available.\n{output}")

        try:
            with urllib.request.urlopen(OPENAPI_URL, timeout=2) as response:
                return json.loads(response.read().decode("utf-8"))
        except (urllib.error.URLError, TimeoutError, json.JSONDecodeError) as exc:
            last_error = str(exc)
            time.sleep(0.5)

    output = read_log(log_path)
    raise TimeoutError(f"Timed out waiting for {OPENAPI_URL}. Last error: {last_error}\n{output}")


def read_log(path: Path) -> str:
    try:
        return path.read_text(encoding="utf-8")
    except Exception:
        return ""


def stop_process(process: subprocess.Popen[str]) -> None:
    if process.poll() is not None:
        return

    process.send_signal(signal.SIGTERM)
    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=5)


def to_yaml(value: object, indent: int = 0) -> str:
    rendered = render_yaml(value, indent)
    return rendered if rendered.endswith("\n") else rendered + "\n"


def render_yaml(value: object, indent: int) -> str:
    prefix = " " * indent
    if isinstance(value, dict):
        if not value:
            return "{}"

        lines: list[str] = []
        for key, child in value.items():
            rendered_key = render_key(str(key))
            if is_scalar(child) or child == {} or child == []:
                lines.append(f"{prefix}{rendered_key}: {render_scalar(child)}")
            else:
                lines.append(f"{prefix}{rendered_key}:")
                lines.append(render_yaml(child, indent + 2).rstrip("\n"))
        return "\n".join(lines) + "\n"

    if isinstance(value, list):
        if not value:
            return "[]"

        lines = []
        for item in value:
            if is_scalar(item) or item == {} or item == []:
                lines.append(f"{prefix}- {render_scalar(item)}")
            else:
                lines.append(f"{prefix}-")
                lines.append(render_yaml(item, indent + 2).rstrip("\n"))
        return "\n".join(lines) + "\n"

    return f"{prefix}{render_scalar(value)}"


def is_scalar(value: object) -> bool:
    return value is None or isinstance(value, (str, int, float, bool))


def render_key(key: str) -> str:
    if re.fullmatch(r"[A-Za-z0-9_.-]+", key):
        return key

    return json.dumps(key)


def render_scalar(value: object) -> str:
    if value is None:
        return "null"
    if value is True:
        return "true"
    if value is False:
        return "false"
    if isinstance(value, (int, float)):
        return str(value)
    if isinstance(value, str):
        return json.dumps(value)
    if value == {}:
        return "{}"
    if value == []:
        return "[]"

    raise TypeError(f"Unsupported scalar: {type(value)!r}")


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(f"error: {exc}", file=sys.stderr)
        raise SystemExit(1)
