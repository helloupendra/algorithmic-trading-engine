#!/usr/bin/env python3
"""Rebuild the reviewed FYERS wheel with supported dependencies and stdlib resources.

No resolver bypass: the rebuilt distribution declares the dependencies actually
installed. Upstream code is unchanged except for the removed pkg_resources use.
"""
import base64
import csv
import hashlib
import io
import json
from pathlib import Path
import urllib.request
import zipfile

VERSION = "3.2.1"
LOCAL = VERSION + "+openfno.1"
UPSTREAM_SHA256 = "86e8d049128c192c26eba557a50e50b21288112cdf054ed8a22c512f1ea0eb40"
ROOT = Path(__file__).resolve().parents[1]


def build():
    with urllib.request.urlopen(f"https://pypi.org/pypi/fyers-apiv3/{VERSION}/json", timeout=30) as response:
        manifest = json.load(response)
    wheel = next(item for item in manifest["urls"] if item["filename"] == f"fyers_apiv3-{VERSION}-py3-none-any.whl")
    with urllib.request.urlopen(wheel["url"], timeout=30) as response:
        raw = response.read()
    if hashlib.sha256(raw).hexdigest() != UPSTREAM_SHA256:
        raise RuntimeError("Upstream wheel checksum mismatch")
    old = f"fyers_apiv3-{VERSION}.dist-info/"
    new = f"fyers_apiv3-{LOCAL}.dist-info/"
    files = {}
    with zipfile.ZipFile(io.BytesIO(raw)) as archive:
        for name in archive.namelist():
            if name.endswith("/RECORD"):
                continue
            data = archive.read(name)
            target = name.replace(old, new)
            if name.endswith("/METADATA"):
                lines = data.decode().splitlines()
                lines = [line for line in lines if not line.startswith("Requires-Dist:")]
                at = lines.index("")
                lines[at:at] = [
                    "Requires-Dist: requests>=2.32.4,<3",
                    "Requires-Dist: aiohttp>=3.13,<4",
                    "Requires-Dist: aws-lambda-powertools>=3,<4",
                    "Requires-Dist: websocket-client>=1.9,<2",
                ]
                data = ("\n".join(lines).replace(f"Version: {VERSION}\n", f"Version: {LOCAL}\n", 1) + "\n").encode()
            if name.endswith(".py") and b"from pkg_resources import resource_filename" in data:
                data = data.replace(b"from pkg_resources import resource_filename",
                    b"from importlib.resources import files as _resource_files\n\ndef resource_filename(package, resource):\n    return str(_resource_files(package).joinpath(resource))")
            files[target] = data
    record = io.StringIO(newline="")
    writer = csv.writer(record, lineterminator="\n")
    for name, data in sorted(files.items()):
        digest = base64.urlsafe_b64encode(hashlib.sha256(data).digest()).decode().rstrip("=")
        writer.writerow([name, "sha256=" + digest, len(data)])
    writer.writerow([new + "RECORD", "", ""])
    files[new + "RECORD"] = record.getvalue().encode()
    destination = ROOT / "vendor" / "python" / f"fyers_apiv3-{LOCAL}-py3-none-any.whl"
    destination.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(destination, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for name, data in sorted(files.items()):
            entry = zipfile.ZipInfo(name, date_time=(2026, 11, 9, 0, 0, 0))
            entry.compress_type = zipfile.ZIP_DEFLATED
            entry.external_attr = 0o644 << 16
            archive.writestr(entry, data)
    print(destination.name, hashlib.sha256(destination.read_bytes()).hexdigest())


if __name__ == "__main__":
    build()
