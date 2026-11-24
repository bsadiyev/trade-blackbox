# Decision log

Newest at the bottom. "Recorded" is the date the decision was written down.
To change a decision, add a new entry that supersedes the old one; do not edit history.

| # | Recorded | Decision | Why | Alternatives considered |
|---|---|---|---|---|
| D1 | 2026-11-24 | One project for recorder and alerts, in Python. The owner is the first user. | Both modules share one event flow; Python is fastest to iterate and fits a hosted service later. | Two projects; TypeScript (better for a desktop exe, irrelevant for a hosted service); C# (needed only for Quantower). |
| D2 | 2026-11-24 | Strictly read-only: no order placement, modification or cancellation. Read-only key recommended. | Trust and safety: a journal must not be able to hurt the account. Complements tools that close positions. | Auto-close at limits (exists elsewhere, e.g. Drawdown Guard). |
| D3 | 2026-11-24 | The raw event log is the source of truth; trades and fills are a pure recomputation. | Metrics improve over time; any day can be rebuilt and tested by replay. | Computing tables live and storing only results. |
| D4 | 2026-11-24 | Record real frames before writing logic against them. | Docs do not describe every payload; guessing API names caused repeated broken builds in Quantower. | Build from docs and fix later. |
| D5 | 2026-11-24 | Excursions valued at the order book midpoint (Binance, Bybit, Hyperliquid); fill prices also feed extremes. | Matches how the firm values positions for loss rules; fills guarantee that MAE is never smaller than the loss. | Mark price; last trade price. |
| D6 | 2026-11-24 | Units: USD and R, plus basis points for cross-market comparison. No ticks. | Perps have no fixed tick value in the futures sense; R is what matters for risk. | Ticks (futures habit). |
| D7 | 2026-11-24 | MAE/MFE measured from the **average** entry price. | Same reference as the result; earlier first-fill reference let MAE be smaller than the loss. | First fill price (kept as a separate column for analysis). |
| D8 | 2026-11-24 | Trading day = midnight `America/New_York`; files rotate by trading day. | It is the firm's daily loss reset, so one file = one daily-limit day. Follows DST automatically. | Midnight UTC. |
| D9 | 2026-11-24 | No silent reconstruction of missed periods. Rows are flagged `incomplete` (`gap`, `no_ticks`, `restart`). | Reconstructed rows look complete but lack stops and price path; flagged gaps are safer. | Rebuild from REST history. |
| D10 | 2026-11-24 | Alert thresholds derived from the account's rules (v0.3), not hard-coded. | Accounts differ in size and limits; fixed numbers from one account do not transfer. | Fixed defaults only. |
| D11 | 2026-11-24 | Repository in English, chat with the owner in Russian. | Public repo read by reviewers and other traders. | Russian everywhere. |
| D12 | 2026-11-24 | One stage = one PR; the owner merges after review. | Keeps "plan first, act after agreement" visible and reversible. | Direct commits to `main`. |
| D13 | 2026-11-24 | Runs locally on the owner's Windows PC for v0.1–v0.3. | Claude's workspace cannot reach the API; local run is simplest and keeps the key on the owner's machine. | Hosted from day one. |
| D14 | 2026-11-24 | Sandbox test trades are placed manually in the web terminal. | Closest to real trading behavior; the tool's key stays read-only. | Scripted orders via a trade-scope key. |
| D15 | 2026-11-24 | Dependencies: `websockets`, `httpx`, `python-dotenv`, `tzdata`. Dev: `pytest`, `ruff`. | Small, well-known libraries; `tzdata` is required for `zoneinfo` on Windows. | `aiohttp` (heavier), hand-written `.env` parser. |
