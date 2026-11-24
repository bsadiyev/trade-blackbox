# Changelog

All notable changes to this project. Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
the Python package follows [Semantic Versioning](https://semver.org/) (`0.x` while in development).
Every entry carries the date the change was made.

## [Unreleased]

### 2026-11-24 — Repository presentation
- README: CI status badge.
- README: settings screenshots of both Quantower modules (`docs/images/`). The BehaviorAlerter screenshot
  was captured on build 14 and is labeled as such.
- README: disclaimer states the project is independent and not affiliated with MyFundedPerps or Quantower.

### 2026-11-24 — Project foundation
- Added `CLAUDE.md` with working rules: plan before action, one stage per PR, definition of done,
  dating rules, safety rules (read-only, key handling, anonymized fixtures).
- Added docs: roadmap, architecture, decision log, API notes (documented vs verified facts and open questions),
  data schema (raw log defined, trades/fills draft), testing (sandbox scenario, fixtures policy), Windows runbook.
- Added Python skeleton `python/` (package `blackbox` 0.1.0.dev0): configuration from `.env`
  with key-prefix validation and key masking, trading-day boundary at New York midnight,
  `python -m blackbox check` (no network calls).
- Added tests (config, trading day across DST, CLI, guard against API-key-shaped strings in tracked files).
- Added CI: ruff and pytest on Ubuntu and Windows (Python 3.12, 3.13), gitleaks secret scan.
- Added `.gitignore`, `.env.example`, pull request template.

### 2026-11-24 — Quantower modules published
- `quantower/TradeLogger.cs` v19 (code dated 2026-09-12), comments cleaned for publication.
- `quantower/BehaviorAlerter.cs` build 15 (2026-11-24): local time now follows the Windows time zone
  instead of a fixed offset parameter; comments cleaned for publication.
- README and MIT license.
