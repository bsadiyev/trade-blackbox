from datetime import UTC, date, datetime

import pytest

from blackbox.days import trading_day, trading_day_start_ms


def ms(*args: int) -> int:
    return int(datetime(*args, tzinfo=UTC).timestamp() * 1000)


@pytest.mark.parametrize(
    ("utc_ms", "expected"),
    [
        # Summer (EDT, UTC-4): the day rolls at 04:00 UTC.
        (ms(2026, 7, 15, 3, 59, 59), date(2026, 7, 14)),
        (ms(2026, 7, 15, 4, 0, 0), date(2026, 7, 15)),
        # Winter (EST, UTC-5): the day rolls at 05:00 UTC.
        (ms(2026, 1, 15, 4, 59, 59), date(2026, 1, 14)),
        (ms(2026, 1, 15, 5, 0, 0), date(2026, 1, 15)),
    ],
)
def test_trading_day_rolls_at_new_york_midnight(utc_ms, expected):
    assert trading_day(utc_ms) == expected


@pytest.mark.parametrize(
    ("day", "expected_utc"),
    [
        # US daylight saving starts 2026-03-08 at 02:00 local: that midnight is still EST.
        (date(2026, 3, 8), ms(2026, 3, 8, 5, 0, 0)),
        (date(2026, 3, 9), ms(2026, 3, 9, 4, 0, 0)),
        # Daylight saving ends 2026-11-01 at 02:00 local: that midnight is still EDT.
        (date(2026, 11, 1), ms(2026, 11, 1, 4, 0, 0)),
        (date(2026, 11, 2), ms(2026, 11, 2, 5, 0, 0)),
    ],
)
def test_trading_day_start_across_dst(day, expected_utc):
    assert trading_day_start_ms(day) == expected_utc


def test_start_and_day_are_consistent():
    day = date(2026, 11, 24)
    start = trading_day_start_ms(day)
    assert trading_day(start) == day
    assert trading_day(start - 1) == date(2026, 11, 23)
