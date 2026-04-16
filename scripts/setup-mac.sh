#!/bin/bash
set -euo pipefail

# Hermes macOS Setup — builds and installs everything on a Mac
# Includes: service, MAUI shell, Ollama config
#
# Usage:
#   ./setup-mac.sh              # Full build + install
#   ./setup-mac.sh --skip-build # Install from existing publish
#   ./setup-mac.sh --service-only  # Service only (no MAUI shell)
#   ./setup-mac.sh --uninstall  # Remove everything

TASK_LABEL="com.hermes.service"
INSTALL_DIR="$HOME/.local/lib/hermes"
SHELL_INSTALL_DIR="/Applications/Hermes.app"
PLIST_PATH="$HOME/Library/LaunchAgents/${TASK_LABEL}.plist"
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WEB_DIR="$REPO_ROOT/src/Hermes.Web"
SERVICE_PROJ="$REPO_ROOT/src/Hermes.Service"
SHELL_PROJ="$REPO_ROOT/src/Hermes.Shell"
CONFIG_DIR="$HOME/.config/hermes"
LOG_DIR="$CONFIG_DIR/logs"
ARCHIVE_DIR="$HOME/Documents/Hermes"

step() { echo "  → $1"; }
warn() { echo "  ⚠️  $1"; }

# ── Uninstall ─────────────────────────────────────────────────────

if [[ "${1:-}" == "--uninstall" ]]; then
    echo "🗑️  Uninstalling Hermes..."

    if launchctl list 2>/dev/null | grep -q "$TASK_LABEL"; then
        step "Unloading launchd agent..."
        launchctl unload "$PLIST_PATH" 2>/dev/null || true
    fi

    [[ -f "$PLIST_PATH" ]] && rm "$PLIST_PATH" && step "Plist removed"
    [[ -d "$INSTALL_DIR" ]] && rm -rf "$INSTALL_DIR" && step "Service removed"
    [[ -d "$SHELL_INSTALL_DIR" ]] && rm -rf "$SHELL_INSTALL_DIR" && step "Shell app removed"

    echo "✅ Hermes uninstalled. Config and archive preserved."
    echo "   Config:  $CONFIG_DIR/"
    echo "   Archive: $ARCHIVE_DIR/"
    exit 0
fi

# ── Prerequisites ─────────────────────────────────────────────────

echo "⚡ Setting up Hermes on macOS..."

# Check .NET SDK
if ! command -v dotnet &> /dev/null; then
    echo "❌ .NET SDK not found. Install from https://dot.net/download"
    exit 1
fi
step ".NET SDK: $(dotnet --version)"

# Check Node.js (for Tailwind CSS build)
if ! command -v npm &> /dev/null; then
    echo "❌ npm not found. Install Node.js from https://nodejs.org"
    exit 1
fi
step "Node.js: $(node --version)"

# Check Ollama
if command -v ollama &> /dev/null; then
    step "Ollama: $(ollama --version 2>/dev/null || echo 'installed')"
else
    warn "Ollama not found — install from https://ollama.com for comprehension/embedding"
fi

# ── Stop existing ─────────────────────────────────────────────────

if launchctl list 2>/dev/null | grep -q "$TASK_LABEL"; then
    step "Stopping existing Hermes..."
    launchctl unload "$PLIST_PATH" 2>/dev/null || true
    sleep 2
fi

# ── Build ─────────────────────────────────────────────────────────

SERVICE_ONLY=false
SKIP_BUILD=false
[[ "${1:-}" == "--service-only" ]] && SERVICE_ONLY=true
[[ "${1:-}" == "--skip-build" ]] && SKIP_BUILD=true

if [[ "$SKIP_BUILD" == "false" ]]; then
    # Install npm deps if needed
    if [[ ! -d "$WEB_DIR/node_modules" ]]; then
        step "Installing npm dependencies..."
        (cd "$WEB_DIR" && npm ci --quiet) 2>&1 | tail -1
    fi

    # Build Blazor UI CSS
    step "Building Blazor UI CSS..."
    (cd "$WEB_DIR" && npm run build:blazor) 2>&1 | tail -1

    # Publish service
    step "Publishing Hermes service (osx-arm64)..."
    dotnet publish "$SERVICE_PROJ" -c Release -r osx-arm64 --self-contained \
        -o "$INSTALL_DIR" --nologo -v q 2>&1 | grep -i error || true

    # Build MAUI shell (if not service-only and workload is available)
    if [[ "$SERVICE_ONLY" == "false" ]]; then
        if dotnet workload list 2>/dev/null | grep -q "maui"; then
            step "Building Hermes Shell (MAUI)..."
            dotnet publish "$SHELL_PROJ" -c Release -f net9.0-maccatalyst \
                -o "$INSTALL_DIR/shell-publish" --nologo -v q 2>&1 | grep -i error || true

            # Move the .app bundle to /Applications
            BUILT_APP=$(find "$INSTALL_DIR/shell-publish" -name "*.app" -maxdepth 1 | head -1)
            if [[ -n "$BUILT_APP" ]]; then
                step "Installing Hermes.app to /Applications..."
                rm -rf "$SHELL_INSTALL_DIR"
                cp -R "$BUILT_APP" "$SHELL_INSTALL_DIR"
                # Copy service into the app bundle for child-process launch
                mkdir -p "$SHELL_INSTALL_DIR/Contents/Resources/service"
                cp -R "$INSTALL_DIR"/* "$SHELL_INSTALL_DIR/Contents/Resources/service/" 2>/dev/null || true
                step "Shell app installed"
            else
                warn "MAUI build produced no .app bundle — skipping shell"
            fi
            rm -rf "$INSTALL_DIR/shell-publish"
        else
            warn "MAUI workload not installed — skipping shell build"
            warn "Install with: dotnet workload install maui-maccatalyst"
        fi
    fi
fi

# Verify service
EXE_PATH="$INSTALL_DIR/Hermes.Service"
if [[ ! -f "$EXE_PATH" ]]; then
    echo "❌ Service not found at $EXE_PATH"
    exit 1
fi
chmod +x "$EXE_PATH"

# ── Config ────────────────────────────────────────────────────────

mkdir -p "$CONFIG_DIR" "$LOG_DIR" "$ARCHIVE_DIR/unclassified"

# Create default config if none exists
if [[ ! -f "$CONFIG_DIR/config.yaml" ]]; then
    step "Creating default config..."
    cat > "$CONFIG_DIR/config.yaml" << 'YAML'
archive_dir: ~/Documents/Hermes

# Gmail OAuth credentials (run gmail-setup first)
credentials: ~/.config/hermes/gmail_credentials.json

# Email accounts to sync (uncomment and configure)
accounts: []
#  - label: your.email@gmail.com
#    provider: gmail
#    backfill:
#      enabled: true
#      batch_size: 200

sync_interval_minutes: 15
min_attachment_size: 20480

watch_folders: []
#  - path: ~/Downloads
#    patterns: ["*.pdf"]

ollama:
  enabled: true
  base_url: http://localhost:11434
  embedding_model: nomic-embed-text
  vision_model: llava
  instruct_model: qwen2.5:32b

fallback:
  embedding: onnx
YAML
fi

# ── Ollama models ─────────────────────────────────────────────────

if command -v ollama &> /dev/null; then
    step "Pulling Ollama models (this may take a while on first run)..."
    ollama pull nomic-embed-text 2>&1 | tail -1 || warn "Failed to pull nomic-embed-text"
    ollama pull qwen2.5:32b 2>&1 | tail -1 || warn "Failed to pull qwen2.5:32b (need ~20GB)"
    ollama pull llava 2>&1 | tail -1 || warn "Failed to pull llava"
fi

# ── launchd agent ─────────────────────────────────────────────────

step "Registering launchd agent..."
mkdir -p "$(dirname "$PLIST_PATH")"
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

# Start the service
step "Starting Hermes..."
launchctl load "$PLIST_PATH"
sleep 3

# Verify
if curl -sf http://localhost:21741/health > /dev/null 2>&1; then
    echo "✅ Hermes is running and healthy!"
else
    warn "Service registered but not yet responding — check logs at $LOG_DIR/"
fi

echo ""
echo "   Service:  $INSTALL_DIR"
echo "   Config:   $CONFIG_DIR/config.yaml"
echo "   Archive:  $ARCHIVE_DIR/"
echo "   Logs:     $LOG_DIR/"
echo "   Web UI:   http://localhost:21741"
if [[ -d "$SHELL_INSTALL_DIR" ]]; then
echo "   App:      $SHELL_INSTALL_DIR"
fi
echo ""
echo "   Default model: qwen2.5:32b (Mac Studio 64GB)"
echo "   Edit config:   nano $CONFIG_DIR/config.yaml"
echo "   View logs:     tail -f $LOG_DIR/hermes.log"
echo "   Stop:          launchctl unload $PLIST_PATH"
echo "   Upgrade:       git pull && ./scripts/setup-mac.sh"
echo "   Uninstall:     ./scripts/setup-mac.sh --uninstall"
