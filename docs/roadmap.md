# Roadmap

Status values: `done`, `in progress`, `next`, `planned`.

## Foundation — done (2026-11-24)

Repository rules (`CLAUDE.md`), docs, CI (lint, tests on Ubuntu and Windows, secret scan),
Python skeleton: configuration loading with key masking, trading-day boundary.

## v0.1 — Recorder

Goal: run it before a session; it writes the raw event log and a trades table with MAE/MFE,
real risk after adds and time without a stop.

| Stage | What | Who | Status |
|---|---|---|---|
| 0. Setup | Python 3.12+, Git, clone, virtual env, `.env` with the sandbox key, `python -m blackbox check` passes | owner | next |
| 1. Record | Connect to the account stream and market data, write every frame as received into `events_<day>.jsonl`. Owner runs the sandbox scenario from `docs/testing.md`, including disconnect tests | Claude builds, owner runs | planned |
| 2. Recorder | Rebuild `fills_<day>.csv` and `trades_<day>.csv` from the raw log. Tests on the recorded, anonymized scenario. `rebuild` command | Claude | planned |
| 3. Resilience | 45 s watchdog, resume with `last_seq`/`since`, REST backfill of fills, `incomplete=gap` | Claude builds, owner tests | planned |
| 4. Release | README quick start, sample output on synthetic data, tag `v0.1.0`, update to the Builders Program | Claude + owner | planned |

Stage 1 is deliberately "record only": the docs do not describe every payload in full,
so the recorder logic is written against real frames, not guesses.

## v0.2 — Behavior alerts

Port of `quantower/BehaviorAlerter.cs`: fast re-entry after a loss, losing streak with countdown,
position without a stop, size increase after the entry window, state counter, session deadline.
Delivery: sound on Windows and Telegram.

## v0.3 — Rules-aware thresholds

Thresholds derived from the account's own rules instead of fixed numbers:
`risk.daily_loss_room`, `risk.max_drawdown_room`, `requirements.*` from `GET /v1/accounts/{id}`
and the effective trading policy. Risk expressed as a share of the remaining daily room.

## Later — Hosted bot

A Telegram bot other traders can connect with a read-only key, without installing anything.
Keys stored encrypted. Not started; depends on v0.1–v0.3 proving useful.
