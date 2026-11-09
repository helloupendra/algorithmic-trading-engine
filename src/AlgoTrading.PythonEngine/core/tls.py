"""One certificate-verification policy for platform API clients."""

import os
from pathlib import Path


def tls_verify(override: bool | str | None = None) -> bool | str:
    """Return Requests' secure default or a CA bundle; never disable verification.

    API_CA_BUNDLE takes precedence over Requests' standard CA environment
    settings. Relative bundle paths are resolved against the repository root,
    so a runner launched by the API uses the same file as a terminal tool.
    """
    configured = os.getenv("VERIFY_SSL", "True").strip().lower()
    if configured not in ("true", "1", "t", "yes", "y") or override is False:
        raise ValueError("TLS verification cannot be disabled. Keep VERIFY_SSL=True and configure API_CA_BUNDLE for a private CA.")
    if override is not None and override is not True and not isinstance(override, str):
        raise ValueError("TLS verification requires True or a CA bundle path.")
    bundle = override if isinstance(override, str) else (
        os.getenv("API_CA_BUNDLE") or os.getenv("REQUESTS_CA_BUNDLE") or os.getenv("CURL_CA_BUNDLE") or None)
    if bundle is None:
        return True
    bundle = bundle.strip()
    if not bundle:
        raise ValueError("The CA bundle path cannot be empty.")
    path = Path(bundle).expanduser()
    if not path.is_absolute():
        path = Path(__file__).resolve().parents[3] / path
    if not path.is_file():
        raise ValueError(f"CA bundle file was not found: {path}")
    return str(path.resolve())
