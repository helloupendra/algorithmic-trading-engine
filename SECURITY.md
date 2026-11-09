# Security Policy

## Reporting a Vulnerability

**Do not open a public issue for security vulnerabilities.**

Report privately through GitHub's [private vulnerability reporting](https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability) — use the **Security** tab → **Report a vulnerability**.

Please include:

- A description of the issue and its impact
- Steps to reproduce, or a proof-of-concept
- Affected component (`AlgoTrading.Api`, `PythonEngine`, `Worker.MarketData`, dashboard, …)
- Any suggested remediation

You can expect an acknowledgement within **72 hours** and a substantive response within **7 days**.

## Scope

This project handles broker API credentials, authentication tokens, and trading instructions. Findings in the following areas are especially relevant:

| Area | Examples |
| :--- | :--- |
| **Credential handling** | Broker keys or JWTs written to logs, error responses, or committed files |
| **Authentication & authorisation** | JWT validation gaps, refresh-token replay, missing authorisation on controllers |
| **Broker session store** | Token exposure through the API surface or the database layer |
| **Risk controls** | Any path that reaches an execution route while bypassing `RiskManagementService` |
| **Injection** | SQL injection in raw queries, command injection in operational tooling |
| **Dependencies** | Known CVEs in NuGet, PyPI, or npm dependencies |

## Operational Security Requirements

Anyone deploying this software is responsible for the following. These are not optional.

### Credentials

- **Never commit a real credential.** `.env`, `secrets.json`, and `appsettings.*.Local.json` are git-ignored; keep it that way.
- Use `dotnet user-secrets` for local .NET development, and environment variables or a managed secret store in production.
- Rotate the FYERS app secret immediately if it is ever exposed — including in a private repository, a screenshot, or a support ticket.
- Generate the JWT signing key with real entropy: `openssl rand -base64 48`. Never ship the placeholder value.

### Network exposure

- Do **not** expose PostgreSQL (5432) or Redis (6379) to the public internet. The default `docker-compose.yml` binds them to the host for local development only.
- Set `REDIS_PASSWORD` and enable `requirepass` for any Redis instance reachable beyond localhost.
- Change the default Grafana admin password before exposing port 3000.
- Python API clients require `VERIFY_SSL=True`; disabling verification is rejected.
  Use `API_CA_BUNDLE` with a PEM CA bundle for private HTTPS. Relative paths resolve
  from the repository root. `REQUESTS_CA_BUNDLE` and `CURL_CA_BUNDLE` are supported
  as fallbacks. Loopback HTTP development does not require a TLS exception.

### Broker account safety

- Run against a paper-trading or sandbox account until a strategy has been validated across multiple market regimes.
- Configure `RiskManagement__MaxDailyLoss` and `RiskManagement__MaxOrdersPerMinute` before enabling any live execution path.
- Verify the kill switch works in your deployment before trading real capital.
- Apply the least-privilege API permissions your broker offers.

## What This Project Does Not Guarantee

This is research and portfolio software, not a certified trading system. There is no warranty of correctness, availability, or fitness for trading. See [LICENSE](LICENSE) and the risk disclaimer in the architecture document.

## Supported Versions

Only the current `main` branch receives security fixes.


### Python API certificate trust

The strategy runner, logic engine, feeds, analysis client and operational API
tools use the shared certificate policy in `core/tls.py`. Client defaults,
login, 401 reauthentication and data requests all verify HTTPS. Per-request
`verify=False` is rejected before credentials are fetched, and global insecure
warning suppression has been removed.

For a private CA, set `API_CA_BUNDLE` to the PEM bundle containing its issuing
certificate chain. It overrides `REQUESTS_CA_BUNDLE` and `CURL_CA_BUNDLE`; with
none configured, Requests uses its normal trust store. An explicit client CA
path overrides these settings and is applied to both sign-in and data calls.
Missing bundle files fail at configuration time. Keep the certificate hostname
valid; trusting a CA does not disable hostname checking.

On rollout, remove any `VERIFY_SSL=False` setting, provision a CA bundle where
private HTTPS is used, and restart Python runners/tools to load the policy.
Existing loopback HTTP development does not need a certificate or an insecure
exception. This policy concerns platform API clients; it does not replace the
internal transport implementations of third-party broker SDKs.
