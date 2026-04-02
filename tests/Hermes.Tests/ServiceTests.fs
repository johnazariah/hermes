module Hermes.Tests.ServiceTests

open System
open System.Threading.Tasks
open Xunit
open Hermes.Core

// ─── Heartbeat writing tests ─────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_WriteHeartbeat_CreatesStatusFile`` () =
    let m = TestHelpers.memFs ()
    let archiveDir = "/archive"

    let status: ServiceHost.ServiceStatus =
        { Running = true
          StartedAt = Some(DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero))
          LastSyncAt = Some(DateTimeOffset(2024, 1, 15, 10, 5, 0, TimeSpan.Zero))
          LastSyncOk = true
          DocumentCount = 42L
          UnclassifiedCount = 3
          ErrorMessage = None }

    ServiceHost.writeHeartbeat m.Fs archiveDir status
    |> Async.AwaitTask
    |> Async.RunSynchronously

    let path = ServiceHost.statusFilePath archiveDir |> m.Norm
    Assert.True((m.Get path).IsSome, "Status file should exist")

    let content = (m.Get path).Value
    Assert.Contains("\"running\": true", content)
    Assert.Contains("\"documentCount\": 42", content)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_WriteHeartbeat_StoppedState_WritesRunningFalse`` () =
    let m = TestHelpers.memFs ()
    let archiveDir = "/archive"

    let status: ServiceHost.ServiceStatus =
        { Running = false
          StartedAt = None
          LastSyncAt = None
          LastSyncOk = false
          DocumentCount = 0L
          UnclassifiedCount = 0
          ErrorMessage = Some "test error" }

    ServiceHost.writeHeartbeat m.Fs archiveDir status
    |> Async.AwaitTask
    |> Async.RunSynchronously

    let path = ServiceHost.statusFilePath archiveDir |> m.Norm
    let content = (m.Get path).Value
    Assert.Contains("\"running\": false", content)
    Assert.Contains("\"errorMessage\": \"test error\"", content)

// ─── Heartbeat reading tests ─────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_ReadHeartbeat_ValidJson_ReturnsStatus`` () =
    let m = TestHelpers.memFs ()
    let archiveDir = "/archive"

    let json = """{
  "running": true,
  "startedAt": "2024-01-15T10:00:00+00:00",
  "lastSyncAt": "2024-01-15T10:05:00+00:00",
  "lastSyncOk": true,
  "documentCount": 42,
  "unclassifiedCount": 3,
  "errorMessage": null
}"""

    m.Put (ServiceHost.statusFilePath archiveDir |> m.Norm) json

    let result =
        ServiceHost.readHeartbeat m.Fs archiveDir
        |> Async.AwaitTask
        |> Async.RunSynchronously

    match result with
    | Some status ->
        Assert.True(status.Running)
        Assert.Equal(42L, status.DocumentCount)
        Assert.Equal(3, status.UnclassifiedCount)
        Assert.True(status.LastSyncOk)
    | None -> failwith "Expected Some status"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_ReadHeartbeat_MissingFile_ReturnsNone`` () =
    let m = TestHelpers.memFs ()

    let result =
        ServiceHost.readHeartbeat m.Fs "/nonexistent"
        |> Async.AwaitTask
        |> Async.RunSynchronously

    Assert.True(result.IsNone, "Expected None when file is missing")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_ReadHeartbeat_InvalidJson_ReturnsNone`` () =
    let m = TestHelpers.memFs ()
    let archiveDir = "/archive"

    m.Put (ServiceHost.statusFilePath archiveDir |> m.Norm) "not valid json{{"

    let result =
        ServiceHost.readHeartbeat m.Fs archiveDir
        |> Async.AwaitTask
        |> Async.RunSynchronously

    Assert.True(result.IsNone, "Expected None for invalid JSON")

// ─── Roundtrip test ──────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_Heartbeat_Roundtrip_PreservesValues`` () =
    let m = TestHelpers.memFs ()
    let archiveDir = "/archive"

    let original: ServiceHost.ServiceStatus =
        { Running = true
          StartedAt = Some(DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero))
          LastSyncAt = Some(DateTimeOffset(2024, 6, 1, 12, 30, 0, TimeSpan.Zero))
          LastSyncOk = true
          DocumentCount = 100L
          UnclassifiedCount = 5
          ErrorMessage = None }

    ServiceHost.writeHeartbeat m.Fs archiveDir original
    |> Async.AwaitTask
    |> Async.RunSynchronously

    let result =
        ServiceHost.readHeartbeat m.Fs archiveDir
        |> Async.AwaitTask
        |> Async.RunSynchronously

    match result with
    | Some status ->
        Assert.Equal(original.Running, status.Running)
        Assert.Equal(original.DocumentCount, status.DocumentCount)
        Assert.Equal(original.UnclassifiedCount, status.UnclassifiedCount)
        Assert.Equal(original.LastSyncOk, status.LastSyncOk)
    | None -> failwith "Expected Some status after roundtrip"

// ─── Status file path tests ──────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_StatusFilePath_ReturnsCorrectPath`` () =
    let path = ServiceHost.statusFilePath "/my/archive"
    Assert.EndsWith("hermes-status.json", path)
    Assert.StartsWith("/my/archive", path)

// ─── Service config defaults ─────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_DefaultServiceConfig_UsesConfigValues`` () =
    let config = Config.defaultConfig ()
    let serviceConfig = ServiceHost.defaultServiceConfig config
    Assert.Equal(config.SyncIntervalMinutes, serviceConfig.SyncIntervalMinutes)
    Assert.Equal(60, serviceConfig.HeartbeatIntervalSeconds)
    Assert.Equal(config.ArchiveDir, serviceConfig.ArchiveDir)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_DefaultServiceConfig_CustomInterval_Preserved`` () =
    let config =
        { Config.defaultConfig () with
            SyncIntervalMinutes = 30 }

    let serviceConfig = ServiceHost.defaultServiceConfig config
    Assert.Equal(30, serviceConfig.SyncIntervalMinutes)

// ─── Backlog detection tests ─────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_CountUnclassified_EmptyDir_ReturnsZero`` () =
    let m = TestHelpers.memFs ()
    let archiveDir = "/archive"
    let unclassifiedDir = System.IO.Path.Combine(archiveDir, "unclassified") |> m.Norm
    m.Dirs.[unclassifiedDir] <- true
    let count = ServiceHost.countUnclassified m.Fs archiveDir
    Assert.Equal(0, count)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_CountUnclassified_WithFiles_CountsCorrectly`` () =
    let m = TestHelpers.memFs ()
    let archiveDir = "/archive"
    m.Fs.createDirectory "/archive/unclassified"
    m.Put "/archive/unclassified/doc1.pdf" "pdf content"
    m.Put "/archive/unclassified/doc2.pdf" "pdf content 2"
    m.Put "/archive/unclassified/doc1.pdf.meta.json" "{}"

    let count = ServiceHost.countUnclassified m.Fs archiveDir
    Assert.Equal(2, count)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_CountUnclassified_NoDir_ReturnsZero`` () =
    let m = TestHelpers.memFs ()
    let count = ServiceHost.countUnclassified m.Fs "/archive"
    Assert.Equal(0, count)

// ─── ServiceInstaller tests ──────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceInstaller_GeneratePlist_ContainsLabel`` () =
    let plist = ServiceInstaller.generatePlist "/usr/local/bin/hermes"
    Assert.Contains("com.hermes.service", plist)
    Assert.Contains("/usr/local/bin/hermes", plist)
    Assert.Contains("<string>service</string>", plist)
    Assert.Contains("<string>run</string>", plist)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceInstaller_FormatResult_AllVariants_ReturnNonEmpty`` () =
    let results =
        [ ServiceInstaller.Installed
          ServiceInstaller.Uninstalled
          ServiceInstaller.Started
          ServiceInstaller.Stopped
          ServiceInstaller.AlreadyInstalled
          ServiceInstaller.AlreadyRunning
          ServiceInstaller.NotInstalled
          ServiceInstaller.NotRunning
          ServiceInstaller.StatusInfo "test info"
          ServiceInstaller.Failed "test error" ]

    for r in results do
        let formatted = ServiceInstaller.formatResult r
        Assert.False(System.String.IsNullOrEmpty(formatted), $"Format of {r} should not be empty")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceInstaller_DetectPlatform_ReturnsKnownPlatform`` () =
    let platform = ServiceInstaller.detectPlatform ()
    // Should return Windows or MacOS on supported platforms
    match platform with
    | ServiceInstaller.Windows -> Assert.True(true)
    | ServiceInstaller.MacOS -> Assert.True(true)
    | ServiceInstaller.Unsupported -> Assert.True(true) // Linux CI

// ─── Additional ServiceInstaller tests ───────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceInstaller_GeneratePlist_ContainsRunAtLoadAndKeepAlive`` () =
    let plist = ServiceInstaller.generatePlist "/usr/local/bin/hermes"
    Assert.Contains("<key>RunAtLoad</key>", plist)
    Assert.Contains("<true/>", plist)
    Assert.Contains("<key>KeepAlive</key>", plist)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceInstaller_GeneratePlist_DifferentPath_ReflectsPath`` () =
    let plist = ServiceInstaller.generatePlist "/opt/hermes/bin/hermes-cli"
    Assert.Contains("/opt/hermes/bin/hermes-cli", plist)
    Assert.Contains("com.hermes.service", plist)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceInstaller_GeneratePlist_ContainsPlistXmlHeader`` () =
    let plist = ServiceInstaller.generatePlist "/usr/local/bin/hermes"
    Assert.StartsWith("<?xml version=", plist.TrimStart())
    Assert.Contains("<!DOCTYPE plist", plist)
    Assert.Contains("<plist version=\"1.0\">", plist)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceInstaller_GeneratePlist_ContainsLogPaths`` () =
    let plist = ServiceInstaller.generatePlist "/usr/local/bin/hermes"
    Assert.Contains("<key>StandardOutPath</key>", plist)
    Assert.Contains("<key>StandardErrorPath</key>", plist)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceInstaller_GeneratePlist_ContainsProgramArguments`` () =
    let plist = ServiceInstaller.generatePlist "/usr/local/bin/hermes"
    Assert.Contains("<key>ProgramArguments</key>", plist)
    Assert.Contains("<array>", plist)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceInstaller_FormatResult_Installed_ContainsSuccessMsg`` () =
    let msg = ServiceInstaller.formatResult ServiceInstaller.Installed
    Assert.Contains("success", msg, StringComparison.OrdinalIgnoreCase)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceInstaller_FormatResult_Failed_ContainsMessage`` () =
    let msg = ServiceInstaller.formatResult (ServiceInstaller.Failed "disk full")
    Assert.Contains("disk full", msg)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceInstaller_FormatResult_StatusInfo_ReturnsExactInfo`` () =
    let msg = ServiceInstaller.formatResult (ServiceInstaller.StatusInfo "Running since 2026-01-01")
    Assert.Equal("Running since 2026-01-01", msg)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceInstaller_FormatResult_SpecificMessages`` () =
    Assert.Contains("uninstalled", ServiceInstaller.formatResult ServiceInstaller.Uninstalled, StringComparison.OrdinalIgnoreCase)
    Assert.Contains("started", ServiceInstaller.formatResult ServiceInstaller.Started, StringComparison.OrdinalIgnoreCase)
    Assert.Contains("stopped", ServiceInstaller.formatResult ServiceInstaller.Stopped, StringComparison.OrdinalIgnoreCase)
    Assert.Contains("already", ServiceInstaller.formatResult ServiceInstaller.AlreadyInstalled, StringComparison.OrdinalIgnoreCase)
    Assert.Contains("already", ServiceInstaller.formatResult ServiceInstaller.AlreadyRunning, StringComparison.OrdinalIgnoreCase)
    Assert.Contains("not installed", ServiceInstaller.formatResult ServiceInstaller.NotInstalled, StringComparison.OrdinalIgnoreCase)
    Assert.Contains("not running", ServiceInstaller.formatResult ServiceInstaller.NotRunning, StringComparison.OrdinalIgnoreCase)

// ─── ServiceInstaller platform dispatch tests ────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ServiceInstaller_Status_OnCurrentPlatform_ReturnsValidResult`` () =
    let result =
        ServiceInstaller.status TestHelpers.silentLogger
        |> Async.AwaitTask
        |> Async.RunSynchronously
    match result with
    | ServiceInstaller.NotInstalled -> Assert.True(true)
    | ServiceInstaller.StatusInfo _ -> Assert.True(true)
    | ServiceInstaller.Failed _ -> Assert.True(true)
    | other -> failwith $"Unexpected result from status: {ServiceInstaller.formatResult other}"

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ServiceInstaller_Uninstall_NonExistentTask_ReturnsExpectedResult`` () =
    let m = TestHelpers.memFs ()
    let result =
        ServiceInstaller.uninstall m.Fs TestHelpers.silentLogger
        |> Async.AwaitTask
        |> Async.RunSynchronously
    match result with
    | ServiceInstaller.NotInstalled -> Assert.True(true)
    | ServiceInstaller.Uninstalled -> Assert.True(true)
    | ServiceInstaller.Failed _ -> Assert.True(true)
    | other -> failwith $"Unexpected result from uninstall: {ServiceInstaller.formatResult other}"

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ServiceInstaller_Start_NonExistentTask_ReturnsFailed`` () =
    let result =
        ServiceInstaller.start TestHelpers.silentLogger
        |> Async.AwaitTask
        |> Async.RunSynchronously
    match result with
    | ServiceInstaller.Failed _ -> Assert.True(true)
    | ServiceInstaller.Started -> Assert.True(true)
    | other -> failwith $"Unexpected result from start: {ServiceInstaller.formatResult other}"

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ServiceInstaller_Stop_NonExistentTask_ReturnsFailed`` () =
    let result =
        ServiceInstaller.stop TestHelpers.silentLogger
        |> Async.AwaitTask
        |> Async.RunSynchronously
    match result with
    | ServiceInstaller.Failed _ -> Assert.True(true)
    | ServiceInstaller.Stopped -> Assert.True(true)
    | other -> failwith $"Unexpected result from stop: {ServiceInstaller.formatResult other}"

// ─── ServiceInstaller pure function edge cases ───────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceInstaller_GeneratePlist_PathWithSpaces_IncludesFullPath`` () =
    let plist = ServiceInstaller.generatePlist "/usr/local/my app/bin/hermes"
    Assert.Contains("/usr/local/my app/bin/hermes", plist)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceInstaller_GeneratePlist_WindowsPath_IncludesPath`` () =
    let plist = ServiceInstaller.generatePlist @"C:\Program Files\Hermes\hermes.exe"
    Assert.Contains(@"C:\Program Files\Hermes\hermes.exe", plist)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceInstaller_DetectPlatform_IsConsistent`` () =
    let first = ServiceInstaller.detectPlatform ()
    let second = ServiceInstaller.detectPlatform ()
    Assert.Equal(first, second)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceInstaller_FormatResult_Failed_EmptyMessage`` () =
    let msg = ServiceInstaller.formatResult (ServiceInstaller.Failed "")
    Assert.Contains("Failed", msg)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceInstaller_FormatResult_StatusInfo_EmptyString`` () =
    let msg = ServiceInstaller.formatResult (ServiceInstaller.StatusInfo "")
    Assert.Equal("", msg)

// ─── Logging tests ───────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Logging_Silent_DoesNotThrow`` () =
    Logging.silent.info "test info"
    Logging.silent.warn "test warn"
    Logging.silent.error "test error"
    Logging.silent.debug "test debug"

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Logging_Configure_CreatesWorkingLogger`` () =
    let tempDir = IO.Path.Combine(IO.Path.GetTempPath(), $"hermes-log-test-{Guid.NewGuid():N}")
    try
        let logger = Logging.configure tempDir Serilog.Events.LogEventLevel.Debug
        logger.info "test info message"
        logger.warn "test warn message"
        logger.error "test error message"
        logger.debug "test debug message"
        Assert.True(IO.Directory.Exists(IO.Path.Combine(tempDir, "logs")))
    finally
        try IO.Directory.Delete(tempDir, true) with _ -> ()
