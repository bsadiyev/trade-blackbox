# Testing

## Automated tests

```
cd python
pytest
ruff check .
```

CI runs both on Ubuntu and Windows (Python 3.12 and 3.13) for every push and pull request,
plus a secret scan of the whole history.

Tests never touch the network. Anything that needs API data runs against fixtures in `python/tests/fixtures/`.

## Fixtures policy

- Fixtures are recorded raw logs (`events_*.jsonl`) from the **sandbox** scenario below.
- Before a fixture enters the repo it is anonymized: account ids, order ids, fill ids, position ids and
  stream ids are replaced with stable fakes (`acc_1`, `ord_1`, …). Prices, sizes and times stay real.
- Live-account logs never become fixtures.
- Each fixture has a short note in `python/tests/fixtures/README.md`: date, scenario steps, anything unusual.

## Sandbox scenario (stage 1)

Placed **manually** in the web terminal on the sandbox account, with the recorder running.
One liquid market (e.g. BTC on Binance), small size. Leave ~30 s between steps so events are easy to tell apart.

| # | Action | What it checks |
|---|---|---|
| 1 | Market entry with a stop attached, close manually | Stop at entry, `initial_risk_usd`, manual exit |
| 2 | Market entry without a stop, add a stop after ~60 s, close manually | `no_stop_sec`, stop appearing later |
| 3 | Entry with a stop, then add to the position with a second order | `adds`, average price, `risk_usd_max`, uncovered size |
| 4 | Entry, then close half, then close the rest | Partial exit inside one trade |
| 5 | Entry with a stop close to price; let the stop hit | `STOP` exit, `bracket_role` on the fill |
| 6 | Entry with a take profit close to price; let it hit | `TAKE` exit |
| 7 | Long, then sell twice the size in one order | Reversal: one trade closes, the next opens |
| 8 | Entry, move the stop once, close | Order `edited` events, risk after the move |

Disconnect tests (position open during each):

| # | Action | What it checks |
|---|---|---|
| D1 | Turn Wi-Fi off for ~30 s, back on | Resume of the same stream (`resumed: true`) |
| D2 | Turn Wi-Fi off for ~3 min, close the position on the phone meanwhile, back on | New stream, replay window, REST backfill, `incomplete=gap` |

After the session: send the day's `events_*.jsonl` file. It is anonymized before it becomes a fixture.
