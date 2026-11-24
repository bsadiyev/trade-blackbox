# Working rules for this repository

Read this file first in every session, then `docs/roadmap.md` for the current stage.

## What this project is

Trade Blackbox: a read-only trade recorder and behavior alert tool for discretionary traders.
`quantower/` holds the original C# modules (working prototypes, used on live futures sessions).
`python/` is the port to the MyFundedPerps API and WebSocket streams, built stage by stage.

## Communication and language

- Talk to the owner in **Russian**.
- Everything committed to the repository is in **English**: code, comments, docs, commit messages, PR texts.
  Exception: the existing comments inside `quantower/*.cs` stay in Russian.

## Process

1. **Plan first, act after agreement.** Propose the plan for a stage, wait for the owner's OK, then build.
2. **One stage = one branch = one pull request.** Branch names: `stage-<n>-<slug>` for roadmap stages,
   `fix-<slug>` for fixes, `docs-<slug>` for documentation-only changes.
3. The PR description follows `.github/pull_request_template.md`: what, why, how it was tested.
   The owner reviews and merges. Merging is the approval.
4. Keep the owner's attention cheap: short status lines, no running commentary, no dramatizing.
   When something breaks, say what broke, the cause, and the fix.

## Definition of done (every PR)

- `ruff check` and `pytest` pass locally and in CI.
- `CHANGELOG.md` has an entry under `[Unreleased]`, dated with **today's actual date** (YYYY-MM-DD).
- Docs touched by the change are updated in the same PR (`docs/data-schema.md`, `docs/api-notes.md`,
  `docs/decisions.md`, `docs/roadmap.md`, `docs/runbook.md` as relevant).
- No API keys, no real account ids, no raw logs, no personal data in the diff.

## Versions and dates

- Python package: SemVer `0.x.y`. A release = git tag `vX.Y.Z` + GitHub Release + CHANGELOG section.
- Use the actual current date only for the change being made now. **Never re-date older entries.**
- Quantower modules keep their own version header (`TradeLogger v19`, `BehaviorAlerter` build number).
  Bump it, with today's date, only when their code changes.

## Facts over guesses

- API behavior is known only when confirmed on recorded frames. Every fact lives in `docs/api-notes.md`
  with a status: `doc` (read in the docs), `sandbox` (seen in recorded sandbox frames), `live`.
- Do not write code against a field name that has no fixture behind it. Record first, then build.
  (Lesson from Quantower: guessed API names cost several broken builds.)
- When the docs and recorded frames disagree, the frames win; note the discrepancy in `api-notes.md`.

## Safety rules (non-negotiable)

- The tool is **read-only**. It never places, modifies or cancels orders, and never calls endpoints
  that do (`cancel-all`, `close-all`, order placement). Read-only API keys are enough and recommended.
- The API key is read only from `.env` / environment. Never print, log, commit or echo it.
  `Config.__repr__` masks it; keep it that way.
- Raw logs and CSV output never enter the repository. Test fixtures are anonymized:
  account ids, order ids and fill ids replaced with stable fakes.
- The market data socket needs no key. Never send the trading key to it.

## Data principles

- The raw event log (`events_<trading-day>.jsonl`) is the source of truth. Every frame is written as received.
- Trades and fills tables are a pure recomputation over the raw log. Any day can be rebuilt.
- Trading day = midnight `America/New_York`, the firm's daily loss reset.
- Rows whose numbers cannot be fully trusted are flagged (`incomplete`), never silently reconstructed.

## Engineering habits

- Small, explicit edits. When editing a large file, change it by explicit boundaries and re-check that
  nothing else moved (Quantower lesson: cutting "to the next region marker" deleted lifecycle methods twice).
- Run `ruff check` and `pytest` before every commit.
- Prefer the standard library; every new dependency is a decision recorded in `docs/decisions.md`.

## Environment facts

- Claude's cloud workspace **cannot reach** MyFundedPerps hosts. The program runs on the owner's Windows PC.
  Claude writes code and tests against recorded fixtures; the owner runs it live and sends sandbox logs.
- Windows needs the `tzdata` package for `zoneinfo` (already a dependency).
- CI runs on Ubuntu and Windows.

## Map

| File | Purpose |
|---|---|
| `docs/roadmap.md` | Stages, current status, next step |
| `docs/architecture.md` | Components and data flow |
| `docs/decisions.md` | Decision log: what, why, alternatives |
| `docs/api-notes.md` | Verified and unverified API facts, open questions |
| `docs/data-schema.md` | Raw log format, trades and fills columns |
| `docs/testing.md` | Sandbox test scenario, fixtures policy, running tests |
| `docs/runbook.md` | Setup and running on Windows, troubleshooting |
