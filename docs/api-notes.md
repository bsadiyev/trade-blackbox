# MyFundedPerps API notes

Status of each fact:
- `doc` — read in the official docs (date in the section header). Not yet seen in real frames.
- `sandbox` — confirmed in recorded sandbox frames (fixture name given).
- `live` — confirmed on a live account.

The API is in beta; endpoints may change. When docs and frames disagree, frames win — record it here.

Sources: <https://docs.myfundedperpetuals.com> (index: `/llms.txt`, spec: `/openapi.yaml`).

## Hosts and keys — doc, 2026-11-24

| What | Live | Sandbox |
|---|---|---|
| REST | `https://developers.myfundedperpetuals.com/v1` | `https://sandbox.myfundedperpetuals.com` (path prefix `/v1` assumed — verify in stage 1) |
| Account stream | `wss://account-stream.myfundedperpetuals.com/v1/stream` | `wss://account-stream.myfundedperpetuals.com/v1/sandbox/stream` |
| Market data | `wss://api-stream.myfundedperpetuals.com/v1/market-data` | same feed as live |

- Key prefixes: `fp_live_` (live only), `fp_test_` (sandbox only). Wrong host → `401`.
- Created in Settings → API Keys: environment, access level (read-only or **Read And Trade**),
  account boundary (all accounts or one). Shown once; only a hash is stored. Up to 5 active keys per environment.
- Test keys require a challenge purchase in the last 90 days (or temporary access).
  Lapsed sandbox → `403 sandbox_access_expired`, stream close code `4001`.
- Sandbox: same engine, endpoints, order types, rate limits and rules as live, real market data.
  $100,000 simulated balance per sandbox account, up to 10 accounts per lifetime. Sandbox accounts can breach.

## Account stream — doc, 2026-11-24

- Auth: `Authorization: Bearer <key>` on the upgrade request. Read-only keys work.
- No subscribe message. Sequence: `hello` (`stream_id`, `environment`, heartbeat interval, `resumed`)
  → one `snapshot` per account (account, working orders, open positions, `latest_fill` = `{id, settled_at}` or null,
  `reason`: `added` / `resync`) → `snapshot_end` → events.
- Events (`type` / `event`):
  - `order`: `created`, `edited`, `triggered`, `partially_filled`, `filled`, `canceled`, `rejected`, `expired`
  - `fill` (no `event`)
  - `position`: `opened`, `changed`, `closed` (`closed` carries `close_reason`)
  - `account`: `updated` (`account`, `locked_out`, `changes`), `removed`
  - `price_alert`: `triggered`
- Every event: `seq`, `time` (epoch ms). Account/order/fill/position events: `account_id`.
- Fill fields: `id`, `order_id`, `client_order_id`, `price`, `size`, `notional`, `fee`, `realized_pnl`, `time`,
  `position_side_after` (`long`/`short`/`flat`), `position_size_after`, `side`, `order_type`, `market_id`, `symbol`.
- Bracket orders: `parent_order_id`, `bracket_role` (`take_profit` / `stop_loss`), `take_profit_order_id`, `stop_loss_order_id`.
  Canceled: `cancel_reason`. Rejected: `reject_reason`, `reject_rule_id`.
- The stream does **not** carry equity or rule room — those come from `GET /v1/accounts/{id}`.
- `seq` starts at 1, +1 per snapshot and event frame. `hello`, `heartbeat`, `pong`, `notice`, `error` have no `seq`.
- Resume: `?resume=<stream_id>&last_seq=<seq>&since=<time>`. Same stream held ~60 s (`resumed: true`, no snapshot).
  Otherwise new stream: replace state with snapshots, replayed events flagged `"replay": true`, window ~100 s,
  `snapshot_end.replay.complete` and `incomplete_accounts`.
- Liveness: `heartbeat` every 15 s, WS ping every 20 s. Client treats 45 s without frames as dead.
  Server closes after 60 s of client silence. Optional client ping: `{"op":"ping","id":1}`.
- Close codes: `1012` restart (reconnect now), `1011` snapshot failed (backoff), `4001` key revoked / sandbox lapsed
  (do not retry), `4008` client too slow (reconnect for snapshot), `4013` at capacity (backoff + jitter).
- Limits: up to 8 sockets per key; `accounts_truncated` notice if a key reaches too many accounts.

## Market data — doc, 2026-11-24

- No authentication. **Never send the trading key to this socket.**
- Subscribe: `{"op":"sub","id":1,"channel":"ticks","payload":{"symbols":["BTCUSDT"],"providers":["binance"]}}`,
  unsubscribe `{"op":"unsub","id":1}`.
- Channels: `ticks` (`provider`, `symbol`, `kind` = `mark`/`mid`/`last`, `price`, `time`, `receivedAt`),
  `books` (full snapshot each time, ≤ 1 per 150 ms), `trades`, `candles` (`1s`…`1M`), `marketStats`, `status`.
- Prices and sizes are decimal **strings**; times are epoch ms.
- Limits: 256 subscriptions/connection, 32 symbols and 6 providers per subscription,
  30 connection admissions per minute per source. Enable `permessage-deflate`.
  Server pings every 30 s; closes after 75 s without inbound frames or pongs.

## REST — doc, 2026-11-24

- `GET /v1/accounts`, `GET /v1/accounts/{id}`: account fields and `risk` snapshot marked from book mids:
  `equity`, `unrealized_pnl`, `daily_loss_floor`, `max_drawdown_floor`, `daily_loss_room`, `max_drawdown_room`,
  `remaining_profit`, `remaining_profit_equity`, `marks_complete`, `missing_markets`,
  `requirements` (`daily_loss_pct`, `max_drawdown_pct`, `profit_target_pct`, `consistency_pct`, `minimum_trading_days`).
  No reset-time field, no environment flag.
- `GET /v1/fills?account_id=…&limit≤100&cursor=…`: newest settlement first, cursor pagination
  (`has_more`, `next_cursor`). Items are **Order objects** with `status = filled`
  (`fill_price`, `filled_size`, `fee`, `realized_pnl`, `position_side_after`, `position_size_after`, …).
- Position object: `id`, `side`, `size`, `entry_price`, `exit_price`, `opened_at`, `closed_at`, `realized_pnl`,
  `fees`, `funding`, `close_reason` (`user`, `take_profit`, `stop_loss`, `liquidation`, `reversal`, `system`).
  Per-trade realized P&L, fees and funding are said to come from closed positions.

## Rules and fees — doc, 2026-11-24

- Daily loss: measured from start-of-day equity at **midnight America/New_York** (04:00 UTC in EDT, 05:00 UTC in EST).
- Max drawdown: **static** floor (starting balance minus allowance), not trailing.
- Loss rules use equity including open P&L, checked whenever the valuation price moves.
  Binance, Bybit, Hyperliquid positions valued at the **order book midpoint**.
- Commission per fill on notional, both directions; crypto 0.03%.
- Swap: hourly charge (crypto 0.03% per day / 24) on size held at each hourly boundary, applied before settlement.
  Venue funding rates do not change the balance.
- Built-in journal: R = net P&L / initial risk from the stop attached at entry; moving the stop does not change it.
  No MAE/MFE. Calendar groups trades by New York trading day.

## Open questions (verify in stage 1)

1. Sandbox REST path: is it `https://sandbox.myfundedperpetuals.com/v1/...`?
2. Stream fill `id` vs REST `GET /v1/fills` items (Order objects): which id matches for backfill
   (`id`, `partial_of_order_id`, `partial_fill_root_order_id`)?
3. Does the stream fill `fee` include swap charges, or do swaps appear only on positions / account balance?
4. Position `funding` vs "venue funding does not change balance": what does `funding` contain — the swap fee?
5. Units of `opened_at` / `closed_at` on positions (ms like the rest?).
6. How a stop attached at entry appears: separate `order` events with `bracket_role = stop_loss`, and when
   (before or after the entry fill)?
7. Partial close by the user: which `close_reason` and `order_type` appear on the reducing fill?
8. Which `ticks.kind` values are actually emitted for each provider (is `mid` always present)?
