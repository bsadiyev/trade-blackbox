"""Command line entry point: ``python -m blackbox <command>``."""

from __future__ import annotations

import argparse
import sys
import time

from . import __version__
from .config import ConfigError, load_config
from .days import trading_day


def _check() -> int:
    try:
        cfg = load_config()
    except ConfigError as exc:
        print(f"Config error: {exc}", file=sys.stderr)
        return 2

    now_ms = int(time.time() * 1000)
    print(f"Trade Blackbox {__version__}")
    print(f"environment  : {cfg.env}")
    print(f"api key      : {cfg.masked_key()}")
    print(f"REST         : {cfg.rest_url}")
    print(f"account WS   : {cfg.account_stream_url}")
    print(f"market data  : {cfg.market_data_url}")
    print(f"data folder  : {cfg.data_dir}")
    print(f"trading day  : {trading_day(now_ms).isoformat()} (New York)")
    print("OK: configuration is valid. No network calls were made.")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="blackbox",
        description="Read-only trade recorder and behavior alerts for MyFundedPerps.",
    )
    parser.add_argument("--version", action="version", version=f"%(prog)s {__version__}")
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("check", help="validate the configuration; makes no network calls")

    args = parser.parse_args(argv)
    if args.command == "check":
        return _check()
    parser.error(f"unknown command {args.command!r}")
    return 2


if __name__ == "__main__":
    sys.exit(main())
