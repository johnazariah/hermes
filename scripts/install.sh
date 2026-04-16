#!/bin/bash
set -euo pipefail

# Hermes Service Installer for macOS
# Usage:
#   ./install.sh              # Build, publish, register
#   ./install.sh --uninstall  # Remove launchd agent, keep data
#   ./install.sh --skip-build # Re-register without rebuilding

TASK_LABEL="com.hermes.service"
INSTALL_DIR="$HOME/.local/lib/hermes"
PLIST_PATH="$HOME/Library/LaunchAgents/${TASK_LABEL}.plist"
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WEB_DIR="$REPO_ROOT/src/Hermes.Web"
SERVICE_PROJ="$REPO_ROOT/src/Hermes.Service"
CONFIG_DIR="$HOME/.config/hermes"
LOG_DIR="$CONFIG_DIR/logs"

step() { echo "  → $1"; }

# ── Uninstall ─────────────────────────────────────────────────────

if [[ "${1:-}" == "--uninstall" ]]; then
    echo "🗑️  Uninstalling Hermes..."

    if launchctl list | grep -q "$TASK_LABEL" 2>/dev/null; then
        step "Unloading launchd agent..."
        launchctl unload "$PLIST_PATH" 2>/dev/null || true
    else
        step "No launchd agent found"
    fi

    if [[ -f "$PLIST_PATH" ]]; then
        rm "$PLIST_PATH"
        step "Plist removed"
    fi

    if [[ -d "$INSTALL_DIR" ]]; then
        read -p "Remove install directory ($INSTALL_DIR)? [y/N] " -n 1 -r
        echo
        if [[ $REPLY =~ ^[Yy]$ ]]; then
            rm -rf "$INSTALL_DIR"
            step "Install directory removed"
        else
            step "Install directory preserved"
        fi
    fi

    echo "✅ Hermes uninstalled. Config and archive preserved."
    echo "   Config:  $CONFIG_DIR/"
    echo "   Archive: ~/Documents/Hermes/"
    exit 0
fi

# ── Install / Upgrade ─────────────────────────────────────────────

echo "⚡ Installing Hermes..."

# Stop existing agent if running
if launchctl list | grep -q "$TASK_LABEL" 2>/dev/null; then
    step "Stopping existing Hermes agent..."
    launchctl unload "$PLIST_PATH" 2>/dev/null || true
    sleep 2
fi

if [[ "${1:-}" != "--skip-build" ]]; then
    # Build Blazor UI CSS (Tailwind)
    step "Building Blazor UI CSS..."
    (cd "$WEB_DIR" && npm run build:blazor) > /dev/null 2>&1

    # Publish .NET service
    step "Publishing to $INSTALL_DIR..."
    dotnet publish "$SERVICE_PROJ" -c Release -r osx-arm64 --self-contained -o "$INSTALL_DIR" > /dev/null 2>&1
fi

# Verify
EXE_PATH="$INSTALL_DIR/Hermes.Service"
if [[ ! -f "$EXE_PATH" ]]; then
    echo "❌ Publish failed — $EXE_PATH not found"
    exit 1
fi
chmod +x "$EXE_PATH"

# Create directories
mkdir -p "$LOG_DIR"
mkdir -p "$(dirname "$PLIST_PATH")"

# Generate launchd plist
step "Creating launchd agent..."
cat > "$PLIST_PATH" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>${TASK_LABEL}</string>
    <key>ProgramArguments</key>
    <array>
        <string>${EXE_PATH}</string>
    </array>
    <key>WorkingDirectory</key>
    <string>${INSTALL_DIR}</string>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <dict>
        <key>SuccessfulExit</key>
        <false/>
    </dict>
    <key>StandardOutPath</key>
    <string>${LOG_DIR}/hermes.log</string>
    <key>StandardErrorPath</key>
    <string>${LOG_DIR}/hermes-error.log</string>
    <key>EnvironmentVariables</key>
    <dict>
        <key>PATH</key>
        <string>/usr/local/bin:/usr/bin:/bin:/opt/homebrew/bin</string>
        <key>HOME</key>
        <string>${HOME}</string>
    </dict>
</dict>
</plist>
EOF

# Load the agent
step "Starting Hermes..."
launchctl load "$PLIST_PATH"
sleep 3

# Verify
if launchctl list | grep -q "$TASK_LABEL"; then
    echo "✅ Hermes installed and running!"
else
    echo "⚠️  Hermes installed but may not be running yet. Check: launchctl list | grep hermes"
fi

echo ""
echo "   Install:  $INSTALL_DIR"
echo "   Config:   $CONFIG_DIR/"
echo "   Archive:  ~/Documents/Hermes/"
echo "   Logs:     $LOG_DIR/"
echo "   Web UI:   http://localhost:21741"
echo ""
echo "   To upgrade: run this script again"
echo "   To stop:    launchctl unload $PLIST_PATH"
echo "   To remove:  ./install.sh --uninstall"
