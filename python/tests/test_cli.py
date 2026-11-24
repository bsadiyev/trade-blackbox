from blackbox.__main__ import main

TEST_KEY = "fp_test_" + "abcdefgh" + "ijkl" + "9f3c"


def test_check_ok_prints_masked_key_only(monkeypatch, tmp_path, capsys):
    monkeypatch.chdir(tmp_path)  # no .env here
    monkeypatch.setenv("FP_ENV", "sandbox")
    monkeypatch.setenv("FP_API_KEY", TEST_KEY)
    monkeypatch.setenv("FP_DATA_DIR", str(tmp_path))

    assert main(["check"]) == 0

    out = capsys.readouterr().out
    assert "fp_test_...9f3c" in out
    assert TEST_KEY not in out
    assert "No network calls were made" in out


def test_check_reports_config_error(monkeypatch, tmp_path, capsys):
    monkeypatch.chdir(tmp_path)
    monkeypatch.delenv("FP_API_KEY", raising=False)
    monkeypatch.setenv("FP_ENV", "sandbox")

    assert main(["check"]) == 2
    assert "FP_API_KEY is not set" in capsys.readouterr().err


def test_check_reads_dotenv_from_current_folder(monkeypatch, tmp_path, capsys):
    monkeypatch.chdir(tmp_path)
    monkeypatch.delenv("FP_API_KEY", raising=False)
    monkeypatch.delenv("FP_ENV", raising=False)
    monkeypatch.setenv("FP_DATA_DIR", str(tmp_path))
    (tmp_path / ".env").write_text(f"FP_ENV=sandbox\nFP_API_KEY={TEST_KEY}\n", encoding="utf-8")

    assert main(["check"]) == 0
    assert "fp_test_...9f3c" in capsys.readouterr().out
