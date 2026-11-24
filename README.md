# Trade Blackbox

A flight recorder and behavior guard for discretionary traders.

Broker journals tell you *what* you traded. They rarely tell you how much heat a trade took before it worked, whether the stop was actually there after you added size, or that you re-entered 20 seconds after a loss for the third time today. Trade Blackbox records that layer and speaks up while it is happening.

It never places, modifies or cancels orders. It reads, records and alerts.

## Status

| Part | Platform | State |
|---|---|---|
| `quantower/TradeLogger.cs` | Quantower (C#) | Working prototype, used on live futures sessions |
| `quantower/BehaviorAlerter.cs` | Quantower (C#) | Working prototype, used on live futures sessions |
| Python service | MyFundedPerps API + WebSocket streams | Planned, see [Roadmap](#roadmap) |

The Quantower modules are where the ideas were tested. The next step is a platform-independent Python version built on an exchange or prop-firm API.

## TradeLogger

Passive trade journal. Listens to every fill on the account, groups fills into round trips (flat → flat) and writes one CSV row per round trip.

What it adds on top of a broker journal:

- **MAE / MFE** in ticks and USD, with timestamps and the position size at MAE. Measured from the average entry price, so `MAE >= |loss|` holds by construction.
- **Real risk, not planned risk.** Stop detected by trigger price; risk is size × quantity-weighted stop distance. Tracks the maximum risk over the life of the position and the **uncovered size** (contracts with no stop behind them) after adds.
- **Exit reason** by order id: `STOP`, `TAKE`, manual exit, manual exit with no stop.
- **Adds and partial exits** counted per order, not per fill.
- **Commission and net result** next to the broker's own numbers, so discrepancies are visible.
- **Data quality flags.** `incomplete` marks rows whose numbers cannot be fully trusted (`restart`, `no_ticks`); `sanity` runs self-checks such as `mae_lt_loss` or `mae_after_exit`.

Output files (weekly rotation by default): `trades_YYYY-Www.csv`, `fills_YYYY-Www.csv`, optional raw `events_YYYY-Www.csv`.

All file and order-collection work runs off the trading threads (background CSV writer, 1-second bracket scan), so the logger does not cause terminal lag at the moment a position closes.

## BehaviorAlerter

Sound and text alerts for execution patterns that usually come before a bad day.

| Alert | Trigger | Level |
|---|---|---|
| A1 | New entry within N seconds after a losing trade | Pause |
| A2 | Losing streak; the streak number is audible (number of low beats) | Pause + countdown |
| A3 | Position without a stop (quiet for the first seconds, then on a schedule, sound from 60 s) | Violation |
| Size increase | Position grew after the entry window closed | Violation |
| A5b | Entry larger than base size right after a loss | Statistics only |
| State counter | Cumulative count of the four behavior events reached a threshold | Limit, once per session |
| Session deadline | N minutes from the first fill, unless closed profit earned an extension | Limit |

Design choices worth noting:

- Loss / scratch / win is decided in one place, by gross result in ticks per contract with a scratch band around zero.
- One user action is never read as two: duplicate fills are ignored, a reversal filled in several parts does not trigger A1, exits reported by fills are confirmed against the platform's position snapshot.
- Session P&L and drawdown from the intraday peak go to the log, not to the screen. A large always-visible P&L number pulls you into trading the number.
- Everything that can block (disk, sound, platform log, platform alerts) runs on one background queue.

Output: `behavior_YYYYMMDD.jsonl` (one JSON event per line), `state_YYYYMMDD.txt` (survives restarts), generated `.wav` files you can replace with your own.

## Building the Quantower modules

1. Visual Studio 2022 or later, new **Class Library** project.
2. Target framework must match your Quantower build (recent versions run on .NET 10: `net10.0-windows`).
3. Add a reference to `TradingPlatform.BusinessLayer.dll` from your Quantower installation and set **Copy Local = false**.
4. Add `TradeLogger.cs` and/or `BehaviorAlerter.cs`, build.
5. Put the built DLL into Quantower's `Settings\Scripts\Strategies` folder (or set it as the build output path) and restart Quantower. The modules appear under **Strategies**.

Code comments and on-screen messages are in Russian. Parameter names and the CSV / JSONL schemas are in English.

## Privacy

The logs contain your account name in the `account` column and in the platform log. Do not commit them: the included `.gitignore` excludes `*.csv`, `*.jsonl` and state files. Strip account identifiers before sharing screenshots.

## Roadmap

Python service for [MyFundedPerps](https://myfundedperpetuals.com) using the public REST API and WebSocket streams:

1. **Ingest.** Account stream (fills, orders, positions, risk snapshot) plus market data for markets with an open position, written to an append-only raw event log. Sequence numbers, resume and REST backfill cover disconnects.
2. **Recorder.** Trades and fills tables are recomputed from the raw log, so every metric can be rebuilt and tested by replay. MAE / MFE measured on book mid, in USD and R.
3. **Behavior guard.** The same rules as BehaviorAlerter, with thresholds derived from the account's own risk rules instead of hard-coded numbers. Alerts to Telegram.
4. **Hosted bot.** Connect with a read-only API key, no local install.

Read-only keys only. The service never needs trading permissions.

## Disclaimer

This is a personal tool shared as-is. It is not financial advice and gives no guarantee that the numbers are correct for your platform or broker. Always check against your broker's statements.

## License

[MIT](LICENSE)
