"""
The repository passes Sentinel's own secret scan.

Sentinel's security agent runs ``git grep`` for secret-shaped strings once a
day on the server, and a hit is a CRITICAL incident (#202, 1 Oct 2026: a made-up
JWT in a C# test fixture without its allow marker). Running the same scan here
puts the hit in CI, before the push, instead of on the server after the deploy.
A fixture that needs a token-shaped string carries ``pragma: allowlist secret``
on its line.
"""
import shutil
import subprocess
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path

from sentinel.agents.security import SecurityAgent
from sentinel.context import SentinelContext

REPO = Path(__file__).resolve().parents[3]


def _in_git_checkout() -> bool:
    if shutil.which("git") is None:
        return False
    done = subprocess.run(["git", "rev-parse", "--is-inside-work-tree"], cwd=REPO, capture_output=True, text=True, check=False)
    return done.returncode == 0 and done.stdout.strip() == "true"


@unittest.skipUnless(_in_git_checkout(), "needs the repository's git checkout")
class RepoSecretScanTests(unittest.TestCase):
    def test_no_tracked_file_has_a_secret_shaped_string_without_its_allow_marker(self):
        with tempfile.TemporaryDirectory() as state:
            ctx = SentinelContext(repo_root=REPO, env={}, api_get=lambda path: None, redis_factory=lambda: None,
                                  state_root=Path(state))
            findings = SecurityAgent()._secret_in_git(ctx, {}, datetime.now(timezone.utc))

        self.assertEqual([], [f"{f.title} ({', '.join(f.evidence)})" for f in findings])


if __name__ == "__main__":
    unittest.main()
