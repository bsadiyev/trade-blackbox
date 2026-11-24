# Architecture

## Overview

One Python process on the trader's machine. Read-only.

```
 MyFundedPerps                                   Trader's PC
 ─────────────                                   ───────────────────────────────────────────
 account stream (wss, key) ──┐
                             ├──► ingest ──► raw log  events_<day>.jsonl   (source of truth)
 market data (wss, no key) ──┤                   │
                             │                   ▼
 REST /v1 (key) ─────────────┘              recorder  (pure function)
   backfill fills,                               │
   account risk & rules                          ├──► fills_<day>.csv
                                                 └──► trades_<day>.csv
                                                 │
                                            behavior guard (v0.2) ──► sound, Telegram
```

## Components

### ingest (v0.1, stage 1 and 3)
- Opens the account stream with the API key (`Authorization: Bearer`).
  Server speaks first: `hello` → one `snapshot` per account → `snapshot_end` → events.
- Tracks `seq` and the time of the last frame. Heartbeats arrive every 15 s;
  45 s of silence = dead connection → reconnect with `resume`, `last_seq`, `since`.
- Opens the market data socket (no key) and subscribes to price ticks only for symbols
  with an open position. Unsubscribes when the position is flat.
- After a gap the replay window may be incomplete: compare `latest_fill.id` from the snapshot
  with the last processed fill and backfill through `GET /v1/fills`.
- Writes every frame, as received, to the raw log with a local receive timestamp and its source.

### raw log
- `events_<trading-day>.jsonl`, one JSON object per line, append-only, flushed per line.
- Trading day = midnight `America/New_York` (the firm's daily loss reset).
- Never edited, never committed. Format: `docs/data-schema.md`.

### recorder (v0.1, stage 2)
- Pure function: raw log in → fills and trades tables out. Same input, same output.
- Rebuilds positions flat-to-flat from fills (`position_size_after`, `position_side_after`).
- Extremes for MAE/MFE come from price ticks while the position is open **and** from fill prices.
- Brackets from order events (`bracket_role`, `parent_order_id`) give the stop at entry, risk after adds,
  uncovered size and time without a stop.
- Flags rows it cannot fully trust (`incomplete`) and runs self-checks (`sanity`).

### behavior guard (v0.2)
- Consumes the same event flow live. Alerts only; never blocks, closes or cancels.

## Pricing policy for excursions

- Binance, Bybit, Hyperliquid positions: order book **midpoint**, the same price the firm uses for loss rules.
- Other venues: to be decided from recorded data (mark vs mid); the column `price_source` states which was used.
- Fill prices always feed the extremes, so `MAE >= |loss|` holds by construction (MAE from average entry).

## Runtime

- Python 3.12+, `asyncio`, `websockets` for both sockets, `httpx` for REST.
- Configuration from environment / `.env` (`python/src/blackbox/config.py`).
- Data directory default: `Documents/TradeBlackbox` in the user's home; override with `FP_DATA_DIR`.
