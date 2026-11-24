# Data schema

Files live in the data directory (default `Documents/TradeBlackbox`, override with `FP_DATA_DIR`).
`<day>` is the trading day: the date in `America/New_York`, rolling over at New York midnight.

| File | Written by | Status |
|---|---|---|
| `events_<day>.jsonl` | ingest | defined, implemented in stage 1 |
| `fills_<day>.csv` | recorder | **draft**, finalized in stage 2 against recorded frames |
| `trades_<day>.csv` | recorder | **draft**, finalized in stage 2 against recorded frames |

All files contain your account ids. Do not commit or share them as they are.

## Raw log: `events_<day>.jsonl`

One JSON object per line, append-only, flushed after every line.

```json
{"recv_ms": 1795500000123, "src": "account", "frame": { ...exactly as received... }}
```

| Field | Meaning |
|---|---|
| `recv_ms` | Local receive time, epoch ms (UTC) |
| `src` | `account` (account stream), `market` (market data), `rest` (REST response), `meta` (recorder's own markers) |
| `frame` | The frame as received, untouched. REST: `{"method", "path", "status", "body"}` |

`meta` frames mark what the program did: `start`, `stop`, `connect`, `disconnect` (with reason),
`resume`, `subscribe`, `unsubscribe`, `backfill`. They make gaps visible when reading the log.

## Fills: `fills_<day>.csv` — draft

One row per fill.

| Column | Meaning |
|---|---|
| `trading_day` | New York trading day |
| `time_utc` | Fill time, ISO 8601 UTC |
| `account_id` | Account |
| `market_id`, `provider`, `symbol` | Market |
| `order_id`, `fill_id` | Ids from the stream |
| `kind` | `ENTRY`, `ADD`, `REDUCE`, `EXIT`, `REVERSE` (by position before/after; `ADD` only for a new order) |
| `order_fill_no` | 1, 2, 3… — fill number within its order |
| `side` | `buy` / `sell` |
| `size`, `price`, `notional` | Fill quantity, price, notional |
| `fee`, `realized_pnl` | As reported on the fill |
| `position_side_after`, `position_size_after` | As reported on the fill |
| `bracket_role` | `stop_loss` / `take_profit` if the fill came from a bracket order |

## Trades: `trades_<day>.csv` — draft

One row per round trip: flat → flat. A reduction keeps the trade open; a reversal closes one trade and opens the next.

| Group | Columns |
|---|---|
| Identity | `trading_day`, `account_id`, `market_id`, `provider`, `symbol`, `side`, `entry_order_id`, `exit_order_id` |
| Time | `entry_time_utc`, `exit_time_utc`, `duration_sec` |
| Size | `entry_size` (whole first order), `max_size`, `adds` (by order), `partial_exits` (by order), `entry_fills`, `exit_fills` |
| Price | `first_entry_price`, `avg_entry`, `avg_exit`, `entry_notional_usd` |
| Result | `result_usd_gross`, `fees_usd`, `swap_usd`, `result_usd_net`, `firm_realized_pnl`, `result_bps` |
| Excursions | `mae_usd`, `mae_bps`, `mae_time_utc`, `size_at_mae`, `mfe_usd`, `mfe_bps`, `mfe_time_utc`, `price_source` |
| Risk | `stop_at_entry`, `initial_risk_usd`, `result_r`, `mae_r`, `mfe_r`, `risk_usd_max`, `size_at_max_risk`, `max_uncovered_size`, `no_stop_sec` |
| Exit | `exit_reason` (`STOP`, `TAKE`, `MANUAL`, `MANUAL_NO_STOP`, `LIQUIDATION`, `REVERSAL`, `SYSTEM`), `close_reason` (as sent by the firm) |
| Quality | `incomplete` (`gap`, `no_ticks`, `restart`; empty = trust the numbers), `sanity` (`ok` or flags such as `mae_lt_loss`) |

Definitions:
- MAE/MFE are measured from the **average** entry price at the moment of the extreme, on order book mid
  (see `docs/architecture.md`), with fill prices included. MAE is never smaller than the loss.
- `initial_risk_usd` = entry size × distance from average entry to the stop present right after entry.
  Empty if there was no stop. `result_r`, `mae_r`, `mfe_r` divide by it.
- `risk_usd_max` = maximum over the trade's life of size × quantity-weighted stop distance.
- `max_uncovered_size` = maximum size not covered by any stop.
- `no_stop_sec` = total seconds the position was open with no stop at all.
