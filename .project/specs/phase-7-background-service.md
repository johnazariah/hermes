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
- [ ] `Microsoft.Extensions.Hosting` `IHost` orchestrates all components:
  - Email sync task (timer-triggered)
  - FileSystemWatcher + classifier task
  - Extraction task (channel-fed)
  - Embedding task (channel-fed)
  - MCP server (HTTP listener)
- [ ] Single process — everything runs in one host
- [ ] Graceful shutdown: `CancellationToken` propagated to all tasks on SIGTERM / SIGINT
- [ ] All tasks drain their channels on shutdown (process remaining items or save for restart)
- [ ] Startup logging: log Ollama availability, configured accounts, archive path, sync interval

### 7.2 — macOS: launchd LaunchAgent
- [ ] `hermes service install` writes a LaunchAgent plist to `~/Library/LaunchAgents/com.hermes.service.plist`:
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
- [ ] `hermes service uninstall` removes the plist and unloads the agent
- [ ] `hermes service start` → `launchctl load` the plist
- [ ] `hermes service stop` → `launchctl unload` the plist
- [ ] Auto-restart on crash via `KeepAlive`

### 7.3 — Windows: Service Registration
- [ ] `hermes service install` registers a Windows Service or Task Scheduler task:
  - **Option A (preferred)**: Windows Service via `Microsoft.Extensions.Hosting.WindowsServices`
    - `UseWindowsService()` in the host builder
    - `sc create` / `sc delete` for install/uninstall
  - **Option B (fallback)**: Task Scheduler task triggered "at logon"
- [ ] `hermes service uninstall` removes the service/task
- [ ] `hermes service start` → `sc start Hermes` or equivalent
- [ ] `hermes service stop` → `sc stop Hermes` or equivalent
- [ ] Auto-restart on failure (service recovery settings: restart after 1 minute)

### 7.4 — Service CLI Commands
- [ ] `hermes service install` — register the service for auto-start
- [ ] `hermes service uninstall` — remove the service registration
- [ ] `hermes service start` — start the service now
- [ ] `hermes service stop` — stop the service now
- [ ] `hermes service status` — report whether the service is running, last sync, document counts
- [ ] `hermes service run` — run the service in the foreground (useful for debugging)

### 7.5 — Sync Scheduling
- [ ] Email sync triggered by a `PeriodicTimer` at `sync_interval_minutes` (default 15)
- [ ] Timer fires even if previous sync is still running → skip (don't overlap)
- [ ] Filesystem watchers run continuously (real-time)
- [ ] Extraction and embedding run continuously, consuming from channels
- [ ] On startup: catch up on any backlog (unclassified files, unextracted docs, un-embedded docs)

### 7.6 — Health & Observability
- [ ] Service writes a heartbeat to a `{config_dir}/hermes.status` file:
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
- [ ] Updated every 60 seconds
- [ ] `hermes service status` reads this file for quick status check
- [ ] If the file is stale (>5 minutes old), consider service dead

---

## Acceptance Criteria

- [ ] `hermes service install` registers the service (appropriate to OS)
- [ ] After a reboot, Hermes starts automatically and begins syncing
- [ ] `hermes service status` correctly reports running/stopped
- [ ] Service auto-restarts after a crash (tested by killing the process)
- [ ] Background sync runs on schedule; new emails are picked up automatically
- [ ] All pipeline stages (classify, extract, embed) run continuously
- [ ] `hermes service stop` → clean shutdown, all channels drained
- [ ] `hermes service run` runs in foreground with console logging (for debugging)
- [ ] Heartbeat file is updated regularly and reflects accurate status
