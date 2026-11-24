"""Configuration from environment variables and an optional `.env` file.

The API key is never printed in full: `Config.__repr__` and `mask_key` hide it,
and error messages never include it.
"""

from __future__ import annotations

import os
from collections.abc import Mapping
from dataclasses import dataclass, field
from pathlib import Path

from dotenv import find_dotenv, load_dotenv

ENVIRONMENTS = ("sandbox", "live")

KEY_PREFIX = {
    "sandbox": "fp_test_",
    "live": "fp_live_",
}

# Sandbox REST path prefix is assumed to match live; see docs/api-notes.md, open question 1.
REST_URL = {
    "sandbox": "https://sandbox.myfundedperpetuals.com/v1",
    "live": "https://developers.myfundedperpetuals.com/v1",
}

ACCOUNT_STREAM_URL = {
    "sandbox": "wss://account-stream.myfundedperpetuals.com/v1/sandbox/stream",
    "live": "wss://account-stream.myfundedperpetuals.com/v1/stream",
}

# Public feed, same for both environments. Never send the API key to it.
MARKET_DATA_URL = "wss://api-stream.myfundedperpetuals.com/v1/market-data"


class ConfigError(Exception):
    """Invalid or missing configuration. Messages never contain the API key."""


def mask_key(key: str) -> str:
    """Return the key prefix and its last four characters, e.g. ``fp_test_...a1b2``."""
    for prefix in KEY_PREFIX.values():
        if key.startswith(prefix):
            rest = key[len(prefix) :]
            tail = rest[-4:] if len(rest) >= 12 else ""
            return f"{prefix}...{tail}"
    return "***"


def default_data_dir() -> Path:
    return Path.home() / "Documents" / "TradeBlackbox"


@dataclass(frozen=True)
class Config:
    env: str
    api_key: str = field(repr=False)
    data_dir: Path

    @property
    def rest_url(self) -> str:
        return REST_URL[self.env]

    @property
    def account_stream_url(self) -> str:
        return ACCOUNT_STREAM_URL[self.env]

    @property
    def market_data_url(self) -> str:
        return MARKET_DATA_URL

    def masked_key(self) -> str:
        return mask_key(self.api_key)

    def __repr__(self) -> str:
        return (
            f"Config(env={self.env!r}, api_key={self.masked_key()!r}, "
            f"data_dir={str(self.data_dir)!r})"
        )

    __str__ = __repr__


def load_config(environ: Mapping[str, str] | None = None) -> Config:
    """Build the configuration.

    With ``environ=None`` the process environment is used, after loading a `.env` file
    from the current directory or its parents (existing variables are not overridden).
    Passing a mapping skips `.env` entirely; tests rely on that.
    """
    if environ is None:
        load_dotenv(find_dotenv(usecwd=True), override=False)
        environ = os.environ

    env = environ.get("FP_ENV", "sandbox").strip().lower() or "sandbox"
    if env not in ENVIRONMENTS:
        raise ConfigError(f"FP_ENV must be one of {', '.join(ENVIRONMENTS)}, got {env!r}")

    key = environ.get("FP_API_KEY", "").strip()
    if not key:
        raise ConfigError("FP_API_KEY is not set (put it in .env, see .env.example)")

    expected = KEY_PREFIX[env]
    if not key.startswith(expected):
        raise ConfigError(
            f"key prefix does not match FP_ENV={env}: expected a key starting with {expected}"
        )

    raw_dir = environ.get("FP_DATA_DIR", "").strip()
    data_dir = Path(raw_dir).expanduser() if raw_dir else default_data_dir()

    return Config(env=env, api_key=key, data_dir=data_dir)
