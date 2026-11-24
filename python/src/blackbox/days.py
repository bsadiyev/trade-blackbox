"""Trading day boundary: midnight America/New_York, the firm's daily loss reset.

New York midnight is 04:00 UTC during daylight saving time and 05:00 UTC otherwise;
`zoneinfo` follows the switch automatically. On Windows this needs the `tzdata` package.
"""

from __future__ import annotations

from datetime import UTC, date, datetime, time
from zoneinfo import ZoneInfo

NEW_YORK = ZoneInfo("America/New_York")


def trading_day(utc_ms: int) -> date:
    """Trading day of an epoch-millisecond timestamp."""
    return datetime.fromtimestamp(utc_ms / 1000, tz=UTC).astimezone(NEW_YORK).date()


def trading_day_start_ms(day: date) -> int:
    """Epoch milliseconds of the New York midnight that opens ``day``."""
    start = datetime.combine(day, time(0, 0), tzinfo=NEW_YORK)
    return int(start.timestamp() * 1000)
