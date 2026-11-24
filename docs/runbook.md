# Runbook (Windows)

## One-time setup

1. **Python 3.12 or newer** from <https://www.python.org/downloads/>.
   In the installer tick **"Add python.exe to PATH"**.
2. **Git for Windows** from <https://git-scm.com/download/win> (default options are fine).
3. Open **PowerShell** and get the code:
   ```powershell
   cd $HOME\Documents
   git clone https://github.com/bsadiyev/trade-blackbox.git
   cd trade-blackbox
   ```
4. Create a virtual environment and install:
   ```powershell
   python -m venv .venv
   .\.venv\Scripts\Activate.ps1
   pip install -e .\python
   ```
   If PowerShell refuses to run `Activate.ps1`, run once:
   `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned`, then activate again.
5. Create the key file: copy `.env.example` to `.env` in the repository root and put your key in it.
   ```powershell
   Copy-Item .env.example .env
   notepad .env
   ```
   `.env` is ignored by git. Never paste the key anywhere else.
6. Check:
   ```powershell
   python -m blackbox check
   ```
   It prints the environment, the masked key, the data folder and the current trading day.
   It does not connect to anything.

## Every session

```powershell
cd $HOME\Documents\trade-blackbox
.\.venv\Scripts\Activate.ps1
git pull
pip install -e .\python    # only needed when dependencies changed
```
Run commands are added here as stages ship.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `FP_API_KEY is not set` | No `.env` in the folder you run from | Run from the repository root, check `.env` exists |
| `key prefix does not match FP_ENV` | `fp_test_` key with `FP_ENV=live` or the reverse | Fix `FP_ENV` or use the right key |
| HTTP `401` | Invalid or revoked key, or wrong environment | Check the key in Settings → API Keys |
| HTTP `403 sandbox_access_expired` / close `4001` | Sandbox access lapsed | Needs a challenge purchase in the last 90 days or renewed access |
| HTTP `429` | Too many connections | Wait for `Retry-After`; make sure only one copy is running |
| `ZoneInfoNotFoundError` | `tzdata` missing | `pip install -e .\python` again |
