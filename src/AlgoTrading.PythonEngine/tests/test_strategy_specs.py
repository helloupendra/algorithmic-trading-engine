"""
Every registered strategy has a specification in docs/strategies/.

Why a test and not a convention: the strategy catalog is discovered from the
package, so a new strategy is launchable the moment its module imports. Nothing
else in the pipeline would notice that nobody wrote down what it does. This
test reads the same registry the runner does, so the two lists cannot drift.

The template (headings, facts block) is described in docs/strategies/README.md;
the checks here are the mechanical part of it. PyYAML is not a dependency of
the engine, so the facts block is parsed as plain `key: value` lines — the
same restriction the API's parser has, which is the point: what passes here
renders in the console.
"""

import os
import re
import unittest
from typing import Dict, List

import _bootstrap  # noqa: F401

from strategies.registry import load_strategy_factories

REPO_ROOT = os.path.abspath(os.path.join(_bootstrap.ENGINE_DIR, "..", ".."))
SPECS_DIR = os.path.join(REPO_ROOT, "docs", "strategies")

# The H2 headings of docs/strategies/README.md, in order. A spec has exactly
# these as its H2s; anything finer goes under H3.
REQUIRED_HEADINGS: List[str] = [
    "Idea",
    "Data it needs",
    "Timeframe",
    "Entry",
    "Position management",
    "Exit",
    "Parameters",
    "Worked example",
    "Limitations",
    "Facts (machine-readable)",
]

# Keys the facts block must carry (README, "Facts (machine-readable)").
REQUIRED_FACT_KEYS = frozenset({
    "name",
    "category",
    "evaluates_on",
    "resolution",
    "data",
    "instruments",
    "default_lots",
    "built_in_exit",
    "added",
    "spec_version",
})

# Debt: strategies that predate the spec rule and still owe a document.
# Remove a name here when its spec is written — the test fails if a listed
# strategy has a spec file, so this set cannot silently go stale. Never add a
# new strategy here: write its spec instead. Empty since 2026-09-11: every
# registered strategy has a spec.
PENDING_SPECS: frozenset[str] = frozenset()

_H2 = re.compile(r"^##\s+(.*?)\s*$")
_FENCE_OPEN = re.compile(r"^```\s*ya?ml\s*$", re.IGNORECASE)
_FENCE_CLOSE = re.compile(r"^```\s*$")


def spec_path(name: str) -> str:
    return os.path.join(SPECS_DIR, name + ".md")


def h2_headings(text: str) -> List[str]:
    """The H2 headings of a Markdown document, in order, fenced code excluded."""
    headings: List[str] = []
    in_fence = False
    for line in text.splitlines():
        if line.startswith("```"):
            in_fence = not in_fence
            continue
        if in_fence:
            continue
        m = _H2.match(line)
        if m:
            headings.append(m.group(1))
    return headings


def facts_block(text: str) -> Dict[str, str]:
    """
    The last fenced yaml block of the document as a flat map. Only `key: value`
    lines are read (comments and blank lines skipped, surrounding quotes
    dropped); a nested value leaves the key with an empty string. The block
    must come after the Facts heading, otherwise it is some other yaml.
    """
    lines = text.splitlines()
    facts_at = -1
    for i, line in enumerate(lines):
        m = _H2.match(line)
        if m and m.group(1) == REQUIRED_HEADINGS[-1]:
            facts_at = i
    if facts_at < 0:
        return {}

    block: Dict[str, str] = {}
    in_block = False
    for line in lines[facts_at + 1:]:
        if not in_block:
            if _FENCE_OPEN.match(line):
                in_block = True
                block = {}
            continue
        if _FENCE_CLOSE.match(line):
            in_block = False
            continue
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            continue
        if line[:1].isspace() or stripped.startswith("- "):
            continue  # nested content: not a top-level key
        key, sep, value = stripped.partition(":")
        if not sep:
            continue
        value = value.split(" #", 1)[0].strip()
        if len(value) >= 2 and value[0] == value[-1] and value[0] in "\"'":
            value = value[1:-1]
        block[key.strip()] = value
    return block


class StrategySpecTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.registered = sorted(load_strategy_factories().keys())
        if not cls.registered:
            raise AssertionError("registry returned no strategies; discovery is broken")

    def test_every_registered_strategy_has_a_spec(self):
        for name in self.registered:
            if name in PENDING_SPECS:
                continue
            with self.subTest(strategy=name):
                path = spec_path(name)
                self.assertTrue(
                    os.path.isfile(path),
                    f"{name} has no spec: write docs/strategies/{name}.md "
                    f"(template in docs/strategies/README.md)",
                )
                with open(path, encoding="utf-8") as fh:
                    text = fh.read()

                self.assertEqual(
                    h2_headings(text), REQUIRED_HEADINGS,
                    f"docs/strategies/{name}.md: H2 headings must be exactly "
                    f"{REQUIRED_HEADINGS}, in that order (use H3 for sub-sections)",
                )

                facts = facts_block(text)
                self.assertTrue(
                    facts,
                    f"docs/strategies/{name}.md: the Facts section needs a fenced yaml "
                    f"block of key: value lines at the end of the file",
                )
                missing = sorted(REQUIRED_FACT_KEYS - facts.keys())
                self.assertFalse(missing, f"docs/strategies/{name}.md: facts block lacks {missing}")
                self.assertEqual(
                    facts.get("name"), name,
                    f"docs/strategies/{name}.md: facts.name is {facts.get('name')!r}, "
                    f"expected the registry name {name!r}",
                )

    def test_pending_list_is_not_stale(self):
        # A spec that exists for a name still listed as pending means the
        # author forgot the last step; the list would then overstate the debt.
        written = sorted(n for n in PENDING_SPECS if os.path.isfile(spec_path(n)))
        self.assertFalse(
            written,
            f"{written} now have a spec file: remove them from PENDING_SPECS in "
            f"{os.path.relpath(__file__, REPO_ROOT)}",
        )

        # A name nobody can launch is not debt either: the strategy was
        # removed or renamed, and the entry is a leftover.
        unknown = sorted(PENDING_SPECS - set(self.registered))
        self.assertFalse(
            unknown,
            f"{unknown} are in PENDING_SPECS but not in the registry: remove them",
        )

    def test_spec_files_belong_to_registered_strategies(self):
        # A spec for a name the registry does not know is unreachable from the
        # console (the API resolves specs by catalog id). Usually a rename.
        if not os.path.isdir(SPECS_DIR):
            self.skipTest("docs/strategies does not exist")
        orphans = sorted(
            f[:-3] for f in os.listdir(SPECS_DIR)
            if f.endswith(".md") and f != "README.md" and f[:-3] not in self.registered
        )
        self.assertFalse(orphans, f"specs without a registered strategy: {orphans}")


if __name__ == "__main__":
    unittest.main()
