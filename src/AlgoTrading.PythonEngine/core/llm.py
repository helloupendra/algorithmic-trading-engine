"""
core/llm.py

One client for the hosted LLMs the desk may ask for a judgement: NVIDIA's
build.nvidia.com endpoint (OpenAI-compatible), free for development, testing,
research and evaluation. NVIDIA counts serving real end users or conducting
business transactions as production, which needs a paid licence, so nothing
here sits in the live trading path: no strategy, risk rule or order waits on
an answer from it.

Models are asked in tiers, each an ordered chain. The first model that answers
wins; a timeout, a refusal to serve (429, 5xx) or an empty answer moves on to
the next. The free tier has no published rate limits and a model can go quiet
for minutes (30 Sep: deepseek-v4.1-flash gave nothing in 90 s while glm-5.3-flash
answered), so a single-model client would fail exactly when it is busiest.

    judge    the hardest reasoning, low volume: validator, risk memo, researcher
    analyst  reading computed data and writing a view
    extract  high volume, cheap: classification, pulling numbers out of text

Every chain can be replaced from the environment, e.g.
LLM_MODELS_JUDGE="nvidia/nemotron-3-ultra-550b-a55b,moonshotai/kimi-k3".

    python -m core.llm "question" [--tier judge] [--system "..."] [--json]
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time
from dataclasses import dataclass, field
from typing import Any, Callable, Optional

import requests

DEFAULT_BASE_URL = "https://integrate.api.nvidia.com/v1"

# Nemotron 3 Ultra first for judgement (owner, 30 Sep): on its first desk
# question it priced a 2-lot option plan's risk at 3,900 against a 1,000 limit
# and rejected it, in 14 s.
DEFAULT_CHAINS: dict[str, tuple[str, ...]] = {
    "judge": ("nvidia/nemotron-3-ultra-550b-a55b", "moonshotai/kimi-k3", "z-ai/glm-5.3"),
    "analyst": ("moonshotai/kimi-k3", "z-ai/glm-5.3", "nvidia/nemotron-3-super-120b-a12b"),
    "extract": ("deepseek-ai/deepseek-v4.1-flash", "nvidia/nemotron-3.5-lightning-30b-a3b", "z-ai/glm-5.3-flash"),
}

# Statuses that say "not now" rather than "your request is wrong": worth the next model.
_RETRYABLE = {408, 409, 425, 429, 500, 502, 503, 504}


class LlmError(RuntimeError):
    """No model in the chain gave a usable answer. The message never carries the key."""


@dataclass
class Attempt:
    model: str
    outcome: str        # "ok", "timeout", "http 429", "empty", "unreachable", "not json"
    seconds: float


@dataclass
class LlmAnswer:
    text: str
    model: str
    seconds: float
    usage: dict = field(default_factory=dict)
    attempts: list[Attempt] = field(default_factory=list)
    data: Any = None    # the parsed object when JSON was asked for


def chain_for(tier: str, env: Optional[dict] = None) -> tuple[str, ...]:
    """The tier's models, from LLM_MODELS_<TIER> when set, else the defaults."""
    env = os.environ if env is None else env
    override = (env.get(f"LLM_MODELS_{tier.upper()}") or "").strip()
    if override:
        return tuple(m.strip() for m in override.split(",") if m.strip())
    if tier not in DEFAULT_CHAINS:
        raise ValueError(f"unknown tier {tier!r}; one of {', '.join(DEFAULT_CHAINS)}")
    return DEFAULT_CHAINS[tier]


def parse_json(text: str) -> Any:
    """The first JSON object or array in a model's answer, fenced or not; ValueError when there is none."""
    fenced = re.search(r"```(?:json)?\s*(.+?)```", text, flags=re.S)
    candidates = [fenced.group(1)] if fenced else []
    candidates.append(text)
    for chunk in candidates:
        chunk = chunk.strip()
        for opener in ("{", "["):
            start = chunk.find(opener)
            while start != -1:
                try:
                    obj, _ = json.JSONDecoder().raw_decode(chunk[start:])
                    return obj
                except json.JSONDecodeError:
                    start = chunk.find(opener, start + 1)
    raise ValueError("no JSON object in the answer")


class LlmClient:
    def __init__(self, api_key: str, base_url: str = DEFAULT_BASE_URL,
                 session: Optional[requests.Session] = None,
                 clock: Callable[[], float] = time.monotonic,
                 env: Optional[dict] = None) -> None:
        if not api_key:
            raise LlmError("NVIDIA_API_KEY is not set (put it in the repo's .env)")
        self._key = api_key
        self._base = base_url.rstrip("/")
        self._session = session or requests.Session()
        self._clock = clock
        self._env = env

    @classmethod
    def from_env(cls, env: Optional[dict] = None) -> "LlmClient":
        if env is None:
            import core.config  # noqa: F401  (loads the repo's .env into os.environ)
            env = dict(os.environ)
        return cls(env.get("NVIDIA_API_KEY", ""), env.get("NVIDIA_API_BASE_URL") or DEFAULT_BASE_URL, env=env)

    def ask(self, prompt: str | list[dict], *, tier: str = "judge", system: Optional[str] = None,
            max_tokens: int = 2048, temperature: float = 0.2, timeout: float = 180.0,
            want_json: bool = False) -> LlmAnswer:
        """Ask the tier's models in order; the first usable answer wins."""
        messages = list(prompt) if isinstance(prompt, list) else [{"role": "user", "content": prompt}]
        if system:
            messages.insert(0, {"role": "system", "content": system})
        attempts: list[Attempt] = []
        for model in chain_for(tier, self._env):
            started = self._clock()
            outcome, body = self._post(model, messages, max_tokens, temperature, timeout)
            seconds = round(self._clock() - started, 2)
            if outcome != "ok":
                attempts.append(Attempt(model, outcome, seconds))
                continue
            text = ((body.get("choices") or [{}])[0].get("message") or {}).get("content") or ""
            if not text.strip():
                # Reasoning models can spend the whole budget thinking and return no content.
                attempts.append(Attempt(model, "empty", seconds))
                continue
            data = None
            if want_json:
                try:
                    data = parse_json(text)
                except ValueError:
                    attempts.append(Attempt(model, "not json", seconds))
                    continue
            attempts.append(Attempt(model, "ok", seconds))
            return LlmAnswer(text.strip(), model, seconds, body.get("usage") or {}, attempts, data)
        tried = "; ".join(f"{a.model}: {a.outcome} after {a.seconds}s" for a in attempts)
        raise LlmError(f"no usable answer from the {tier} chain ({tried})")

    def _post(self, model: str, messages: list[dict], max_tokens: int, temperature: float,
              timeout: float) -> tuple[str, dict]:
        try:
            r = self._session.post(
                f"{self._base}/chat/completions", timeout=timeout,
                headers={"Authorization": f"Bearer {self._key}", "Content-Type": "application/json"},
                json={"model": model, "messages": messages, "max_tokens": max_tokens,
                      "temperature": temperature})
        except requests.Timeout:
            return "timeout", {}
        except requests.RequestException as exc:
            # The exception text can carry the request; only its type is kept.
            return f"unreachable ({type(exc).__name__})", {}
        if r.status_code in _RETRYABLE:
            return f"http {r.status_code}", {}
        if r.status_code >= 400:
            # 400/401/403/404 will not change on the next try of this model, but
            # another model may exist where this one does not (404), so move on.
            return f"http {r.status_code}", {}
        try:
            return "ok", r.json()
        except ValueError:
            return "not json", {}


def main(argv: Optional[list[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="Ask the desk's LLM chain one question.")
    parser.add_argument("prompt")
    parser.add_argument("--tier", default="judge", choices=sorted(DEFAULT_CHAINS))
    parser.add_argument("--system")
    parser.add_argument("--json", action="store_true", help="require a JSON answer")
    parser.add_argument("--max-tokens", type=int, default=2048)
    args = parser.parse_args(argv)
    try:
        answer = LlmClient.from_env().ask(args.prompt, tier=args.tier, system=args.system,
                                          max_tokens=args.max_tokens, want_json=args.json)
    except LlmError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1
    print(f"[{answer.model} · {answer.seconds}s · {answer.usage.get('completion_tokens', '?')} tokens]")
    print(json.dumps(answer.data, indent=2, ensure_ascii=False) if args.json else answer.text)
    return 0


if __name__ == "__main__":
    sys.exit(main())
