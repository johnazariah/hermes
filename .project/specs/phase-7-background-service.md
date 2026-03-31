# Phase 7: Background Service

**Status**: Not Started  
**Depends On**: Phase 1 (Email Sync), Phase 2 (Classification), Phase 3 (Text Extraction)  
**Deliverable**: `hermes service install` → Hermes starts on login and runs forever.

---

## Objective

Run Hermes as a persistent background service that survives reboots, manages all pipeline tasks, and provides lifecycle control via CLI.

---

## Tasks

### 7.1 — Service Host
- [x] `Microsoft.Extensions.Hosting` `IHost` orchestrates all components:
  - Email sync task (timer-triggered)
  - FileSystemWatcher + classifier task
  - Extraction task (channel-fed)
  - Embedding task (channel-fed)
  - MCP server (HTTP listener)
- [x] Single process — everything runs in one host
- [x] Graceful shutdown: `CancellationToken` propagated to all tasks on SIGTERM / SIGINT
- [x] All tasks drain their channels on shutdown (process remaining items or save for restart)
- [x] Startup logging: log Ollama availability, configured accounts, archive path, sync interval

### 7.2 — macOS: launchd LaunchAgent
- [x] `hermes service install` writes a LaunchAgent plist to `~/Library/LaunchAgents/com.hermes.service.plist`:
  ```xml
  <?xml version="1.0" encoding="UTF-8"?>
  <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
    "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
  <plist version="1.0">
  <dict>
      <key>Label</key>
      <string>com.hermes.service</string>
      <key>ProgramArguments</key>
      <array>
          <string>/usr/local/bin/hermes</string>
          <string>service</string>
          <string>run</string>
      </array>
      <key>RunAtLoad</key>
      <true/>
      <key>KeepAlive</key>
      <true/>
      <key>StandardOutPath</key>
      <string>/tmp/hermes.stdout.log</string>
      <key>StandardErrorPath</key>
      <string>/tmp/hermes.stderr.log</string>
  </dict>
  </plist>
  ```
- [x] `hermes service uninstall` removes the plist and unloads the agent
- [x] `hermes service start` → `launchctl load` the plist
- [x] `hermes service stop` → `launchctl unload` the plist
- [x] Auto-restart on crash via `KeepAlive`

### 7.3 — Windows: Service Registration
- [x] `hermes service install` registers a Windows Service or Task Scheduler task:
  - **Option A (preferred)**: Windows Service via `Microsoft.Extensions.Hosting.WindowsServices`
    - `UseWindowsService()` in the host builder
    - `sc create` / `sc delete` for install/uninstall
  - **Option B (fallback)**: Task Scheduler task triggered "at logon"
- [x] `hermes service uninstall` removes the service/task
- [x] `hermes service start` → `sc start Hermes` or equivalent
- [x] `hermes service stop` → `sc stop Hermes` or equivalent
- [x] Auto-restart on failure (service recovery settings: restart after 1 minute)

### 7.4 — Service CLI Commands
- [x] `hermes service install` — register the service for auto-start
- [x] `hermes service uninstall` — remove the service registration
- [x] `hermes service start` — start the service now
- [x] `hermes service stop` — stop the service now
- [x] `hermes service status` — report whether the service is running, last sync, document counts
- [x] `hermes service run` — run the service in the foreground (useful for debugging)

### 7.5 — Sync Scheduling
- [x] Email sync triggered by a `PeriodicTimer` at `sync_interval_minutes` (default 15)
- [x] Timer fires even if previous sync is still running → skip (don't overlap)
- [x] Filesystem watchers run continuously (real-time)
- [x] Extraction and embedding run continuously, consuming from channels
- [x] On startup: catch up on any backlog (unclassified files, unextracted docs, un-embedded docs)

### 7.6 — Health & Observability
- [x] Service writes a heartbeat to a `{config_dir}/hermes.status` file:
  ```json
  {
    "running": true,
    "pid": 12345,
    "started_at": "2025-03-27T10:00:00Z",
    "last_sync": "2025-03-27T10:15:00Z",
    "documents_total": 1234,
    "ollama_available": true,
    "mcp_port": 21740
  }
  ```
- [x] Updated every 60 seconds
- [x] `hermes service status` reads this file for quick status check
- [x] If the file is stale (>5 minutes old), consider service dead

---

## Acceptance Criteria

- [x] `hermes service install` registers the service (appropriate to OS)
- [x] After a reboot, Hermes starts automatically and begins syncing
- [x] `hermes service status` correctly reports running/stopped
- [x] Service auto-restarts after a crash (tested by killing the process)
- [x] Background sync runs on schedule; new emails are picked up automatically
- [x] All pipeline stages (classify, extract, embed) run continuously
- [x] `hermes service stop` → clean shutdown, all channels drained
- [x] `hermes service run` runs in foreground with console logging (for debugging)
- [x] Heartbeat file is updated regularly and reflects accurate status
