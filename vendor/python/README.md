# Reviewed FYERS compatibility wheel

`fyers_apiv3-3.2.1+openfno.1-py3-none-any.whl` derives from the MIT-licensed
[official FYERS 3.2.1 distribution](https://pypi.org/project/fyers-apiv3/3.2.1/).
The original license is retained inside the wheel.

Upstream SHA-256: `86e8d049128c192c26eba557a50e50b21288112cdf054ed8a22c512f1ea0eb40`.
Local SHA-256: `be382169aab907d5a15986c343f41f02368032a9bf9b181e64944f4cb02b6916`.
Rebuild from the repository root: `python scripts/build_fyers_wheel.py`.
The script verifies upstream bytes, uses fixed ZIP metadata and regenerates RECORD.

The upstream metadata pins requests 2.31.0, aiohttp 3.9.3, websocket-client 1.6.1,
aws-lambda-powertools 1.25.5 and setuptools 68.0.0, and installs an obsolete asyncio
backport. Installing upgraded dependencies with `--no-deps` would leave a broken
package contract. This wheel instead declares the supported dependency ranges,
removes the asyncio/setuptools dependencies, and replaces `pkg_resources.resource_filename`
with `importlib.resources.files` in the websocket modules. No trading or transport
logic is changed. The checked-in lock supplies exact versions and hashes.

`test_fyers_compatibility.py` checks synchronous requests, an asynchronous profile
request over real loopback HTTP, logger construction and websocket resource loading.
It does not place an order or prove live broker authentication. The rest of the
engine suite covers the engine's adapters and strategies.

The local version has no separate PyPI advisory entry. `scripts/audit_python.py`
explicitly maps this exact local version to upstream 3.2.1 for source advisories;
all installed dependency versions are audited unchanged. This is a version mapping,
not an advisory ignore. Any future source patch requires a renewed review and version.
