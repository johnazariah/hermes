# Gmail OAuth Client Credentials

Place `gmail_credentials.json` here. This is the OAuth **client ID** file
(the app's identity with Google), not user tokens.

Get it from: https://console.cloud.google.com/apis/credentials

The file is `.gitignore`'d — it ships with the installer but not in source control.

## Resolution order

Hermes looks for `gmail_credentials.json` in this order:
1. Path specified in `config.yaml` → `credentials:` field
2. User config directory → `~/.config/hermes/` (macOS) or `%APPDATA%\hermes\` (Windows)
3. App binary directory → next to `Hermes.Service` executable
4. App credentials directory → `credentials/` subdirectory next to executable
