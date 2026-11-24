import pytest

from blackbox.config import (
    ACCOUNT_STREAM_URL,
    MARKET_DATA_URL,
    REST_URL,
    ConfigError,
    default_data_dir,
    load_config,
    mask_key,
)

# Fake keys are built by concatenation so no key-shaped literal sits in the source.
TEST_KEY = "fp_test_" + "abcdefgh" + "ijkl" + "9f3c"
LIVE_KEY = "fp_live_" + "abcdefgh" + "ijkl" + "77aa"


def test_sandbox_is_the_default():
    cfg = load_config({"FP_API_KEY": TEST_KEY})
    assert cfg.env == "sandbox"
    assert cfg.rest_url == REST_URL["sandbox"]
    assert cfg.account_stream_url == ACCOUNT_STREAM_URL["sandbox"]
    assert cfg.market_data_url == MARKET_DATA_URL
    assert cfg.data_dir == default_data_dir()


def test_live_environment():
    cfg = load_config({"FP_ENV": "LIVE", "FP_API_KEY": LIVE_KEY})
    assert cfg.env == "live"
    assert cfg.rest_url == REST_URL["live"]
    assert cfg.account_stream_url == ACCOUNT_STREAM_URL["live"]


def test_missing_key():
    with pytest.raises(ConfigError, match="FP_API_KEY is not set"):
        load_config({"FP_ENV": "sandbox"})


def test_blank_key():
    with pytest.raises(ConfigError, match="FP_API_KEY is not set"):
        load_config({"FP_API_KEY": "   "})


def test_unknown_environment():
    with pytest.raises(ConfigError, match="FP_ENV must be one of"):
        load_config({"FP_ENV": "paper", "FP_API_KEY": TEST_KEY})


@pytest.mark.parametrize(
    ("env", "key"),
    [("live", TEST_KEY), ("sandbox", LIVE_KEY)],
)
def test_prefix_mismatch_does_not_leak_the_key(env, key):
    with pytest.raises(ConfigError) as excinfo:
        load_config({"FP_ENV": env, "FP_API_KEY": key})
    message = str(excinfo.value)
    assert "prefix does not match" in message
    assert key not in message


def test_repr_masks_the_key():
    cfg = load_config({"FP_API_KEY": TEST_KEY})
    for text in (repr(cfg), str(cfg), f"{cfg}"):
        assert TEST_KEY not in text
        assert "fp_test_...9f3c" in text


def test_mask_key_short_and_foreign_keys():
    assert mask_key("fp_test_" + "abc") == "fp_test_..."
    assert mask_key("not-a-key") == "***"


def test_data_dir_override(tmp_path):
    cfg = load_config({"FP_API_KEY": TEST_KEY, "FP_DATA_DIR": str(tmp_path)})
    assert cfg.data_dir == tmp_path


def test_explicit_mapping_ignores_process_environment(monkeypatch):
    monkeypatch.setenv("FP_API_KEY", TEST_KEY)
    with pytest.raises(ConfigError):
        load_config({})
