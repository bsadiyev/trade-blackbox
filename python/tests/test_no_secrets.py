"""Guard: no API-key-shaped string may be tracked by git.

Runs on tracked files only (`git ls-files`), so a local, ignored `.env` does not trip it.
"""

import re
import shutil
import subprocess
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
KEY_PATTERN = re.compile(r"fp_(?:live|test)_[A-Za-z0-9]{16,}")


def _tracked_files() -> list[Path]:
    if shutil.which("git") is None or not (REPO_ROOT / ".git").exists():
        pytest.skip("not a git checkout")
    out = subprocess.run(
        ["git", "ls-files", "-z"], cwd=REPO_ROOT, capture_output=True, check=True
    ).stdout
    return [REPO_ROOT / name for name in out.decode("utf-8").split("\0") if name]


def test_no_api_keys_in_tracked_files():
    offenders = []
    for path in _tracked_files():
        if not path.is_file():
            continue
        text = path.read_bytes().decode("utf-8", errors="ignore")
        if KEY_PATTERN.search(text):
            offenders.append(str(path.relative_to(REPO_ROOT)))
    assert offenders == [], f"API-key-shaped strings found in: {offenders}"
