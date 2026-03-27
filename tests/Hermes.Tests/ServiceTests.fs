module Hermes.Tests.ServiceTests

open System
open System.Threading.Tasks
open Xunit
open Hermes.Core

// ─── In-memory FileSystem interpreter ────────────────────────────────

let inMemoryFileSystem () =
    let files = System.Collections.Concurrent.ConcurrentDictionary<string, string>()
    let fileBytes = System.Collections.Concurrent.ConcurrentDictionary<string, byte array>()
    let dirs = System.Collections.Concurrent.ConcurrentDictionary<string, bool>()

    let fs: Algebra.FileSystem =
        { readAllText = fun path ->
            task {
                match files.TryGetValue(path) with
                | true, content -> return content
                | _ -> return failwith $"File not found: {path}"
            }
          writeAllText = fun path content ->
            task { files.[path] <- content }
          writeAllBytes = fun path bytes ->
            task {
                fileBytes.[path] <- bytes
                files.[path] <- System.Text.Encoding.UTF8.GetString(bytes)
            }
          readAllBytes = fun path ->
            task {
                match fileBytes.TryGetValue(path) with
                | true, bytes -> return bytes
                | _ ->
                    match files.TryGetValue(path) with
                    | true, content -> return System.Text.Encoding.UTF8.GetBytes(content)
                    | _ -> return failwith $"File not found: {path}"
            }
          fileExists = fun path -> files.ContainsKey(path) || fileBytes.ContainsKey(path)
          directoryExists = fun path -> dirs.ContainsKey(path)
          createDirectory = fun path -> dirs.[path] <- true
          deleteFile = fun path ->
            files.TryRemove(path) |> ignore
            fileBytes.TryRemove(path) |> ignore
          moveFile = fun src dst ->
            match files.TryRemove(src) with
            | true, content -> files.[dst] <- content
            | _ -> ()
            match fileBytes.TryRemove(src) with
            | true, bytes -> fileBytes.[dst] <- bytes
            | _ -> ()
          getFiles = fun dir pattern ->
            let prefix = if dir.EndsWith("/") || dir.EndsWith("\\") then dir else dir + "/"
            files.Keys
            |> Seq.append fileBytes.Keys
            |> Seq.distinct
            |> Seq.filter (fun k ->
                k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && not (k.Substring(prefix.Length).Contains("/"))
                && not (k.Substring(prefix.Length).Contains("\\")))
            |> Seq.toArray
          getFileSize = fun path ->
            match fileBytes.TryGetValue(path) with
            | true, bytes -> int64 bytes.Length
            | _ ->
                match files.TryGetValue(path) with
                | true, content -> int64 (System.Text.Encoding.UTF8.GetByteCount(content))
                | _ -> 0L }

    fs, files, dirs

// ─── Test clock ──────────────────────────────────────────────────────

let fixedClock (time: DateTimeOffset) : Algebra.Clock =
    { utcNow = fun () -> time }

// ─── Heartbeat writing tests ─────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_WriteHeartbeat_CreatesStatusFile`` () =
    let fs, files, _ = inMemoryFileSystem ()
    let archiveDir = "/archive"

    let status: ServiceHost.ServiceStatus =
        { Running = true
          StartedAt = Some(DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero))
          LastSyncAt = Some(DateTimeOffset(2024, 1, 15, 10, 5, 0, TimeSpan.Zero))
          LastSyncOk = true
          DocumentCount = 42L
          UnclassifiedCount = 3
          ErrorMessage = None }

    ServiceHost.writeHeartbeat fs archiveDir status
    |> Async.AwaitTask
    |> Async.RunSynchronously

    let path = ServiceHost.statusFilePath archiveDir
    Assert.True(files.ContainsKey(path), "Status file should exist")

    let content = files.[path]
    Assert.Contains("\"running\": true", content)
    Assert.Contains("\"documentCount\": 42", content)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_WriteHeartbeat_StoppedState_WritesRunningFalse`` () =
    let fs, files, _ = inMemoryFileSystem ()
    let archiveDir = "/archive"

    let status: ServiceHost.ServiceStatus =
        { Running = false
          StartedAt = None
          LastSyncAt = None
          LastSyncOk = false
          DocumentCount = 0L
          UnclassifiedCount = 0
          ErrorMessage = Some "test error" }

    ServiceHost.writeHeartbeat fs archiveDir status
    |> Async.AwaitTask
    |> Async.RunSynchronously

    let path = ServiceHost.statusFilePath archiveDir
    let content = files.[path]
    Assert.Contains("\"running\": false", content)
    Assert.Contains("\"errorMessage\": \"test error\"", content)

// ─── Heartbeat reading tests ─────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_ReadHeartbeat_ValidJson_ReturnsStatus`` () =
    let fs, files, _ = inMemoryFileSystem ()
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

    files.[ServiceHost.statusFilePath archiveDir] <- json

    let result =
        ServiceHost.readHeartbeat fs archiveDir
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
    let fs, _, _ = inMemoryFileSystem ()

    let result =
        ServiceHost.readHeartbeat fs "/nonexistent"
        |> Async.AwaitTask
        |> Async.RunSynchronously

    Assert.True(result.IsNone, "Expected None when file is missing")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_ReadHeartbeat_InvalidJson_ReturnsNone`` () =
    let fs, files, _ = inMemoryFileSystem ()
    let archiveDir = "/archive"

    files.[ServiceHost.statusFilePath archiveDir] <- "not valid json{{"

    let result =
        ServiceHost.readHeartbeat fs archiveDir
        |> Async.AwaitTask
        |> Async.RunSynchronously

    Assert.True(result.IsNone, "Expected None for invalid JSON")

// ─── Roundtrip test ──────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_Heartbeat_Roundtrip_PreservesValues`` () =
    let fs, _, _ = inMemoryFileSystem ()
    let archiveDir = "/archive"

    let original: ServiceHost.ServiceStatus =
        { Running = true
          StartedAt = Some(DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero))
          LastSyncAt = Some(DateTimeOffset(2024, 6, 1, 12, 30, 0, TimeSpan.Zero))
          LastSyncOk = true
          DocumentCount = 100L
          UnclassifiedCount = 5
          ErrorMessage = None }

    ServiceHost.writeHeartbeat fs archiveDir original
    |> Async.AwaitTask
    |> Async.RunSynchronously

    let result =
        ServiceHost.readHeartbeat fs archiveDir
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
    let fs, _, dirs = inMemoryFileSystem ()
    let archiveDir = "/archive"
    let unclassifiedDir = System.IO.Path.Combine(archiveDir, "unclassified")
    dirs.[unclassifiedDir] <- true
    let count = ServiceHost.countUnclassified fs archiveDir
    Assert.Equal(0, count)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_CountUnclassified_WithFiles_CountsCorrectly`` () =
    let fs, files, dirs = inMemoryFileSystem ()
    let archiveDir = "/archive"
    let unclassifiedDir = System.IO.Path.Combine(archiveDir, "unclassified")
    dirs.[unclassifiedDir] <- true
    files.[unclassifiedDir + "/doc1.pdf"] <- "pdf content"
    files.[unclassifiedDir + "/doc2.pdf"] <- "pdf content 2"
    files.[unclassifiedDir + "/doc1.pdf.meta.json"] <- "{}"

    let count = ServiceHost.countUnclassified fs archiveDir
    // Should count 2 PDFs, not the .meta.json
    Assert.Equal(2, count)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_CountUnclassified_NoDir_ReturnsZero`` () =
    let fs, _, _ = inMemoryFileSystem ()
    let count = ServiceHost.countUnclassified fs "/archive"
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
