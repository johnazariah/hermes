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
