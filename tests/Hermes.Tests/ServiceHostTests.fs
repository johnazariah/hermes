module Hermes.Tests.ServiceHostTests

open System
open Xunit
open Hermes.Core

// ─── Heartbeat write + read ──────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_WriteAndReadHeartbeat_RoundTrips`` () =
    task {
        let m = TestHelpers.memFs ()
        let archiveDir = "/test/archive"
        m.Fs.createDirectory archiveDir

        let status : ServiceHost.ServiceStatus =
            { Running = true
              StartedAt = Some (DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero))
              LastSyncAt = Some (DateTimeOffset(2026, 3, 15, 10, 30, 0, TimeSpan.Zero))
              LastSyncOk = true
              DocumentCount = 42
              UnclassifiedCount = 3
              ErrorMessage = None }

        do! ServiceHost.writeHeartbeat m.Fs archiveDir status
        let! read = ServiceHost.readHeartbeat m.Fs archiveDir

        Assert.True(read.IsSome)
        let s = read.Value
        Assert.True(s.Running)
        Assert.Equal(42L, s.DocumentCount)
        Assert.Equal(3, s.UnclassifiedCount)
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_ReadHeartbeat_NoFile_ReturnsNone`` () =
    task {
        let m = TestHelpers.memFs ()
        let! result = ServiceHost.readHeartbeat m.Fs "/nonexistent"
        Assert.True(result.IsNone)
    }

// ─── Backlog detection ───────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_CountUnclassified_EmptyDir_ReturnsZero`` () =
    let m = TestHelpers.memFs ()
    let count = ServiceHost.countUnclassified m.Fs "/test/archive"
    Assert.Equal(0, count)

// ─── Sync trigger ────────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_RequestSync_CreatesFile`` () =
    let archiveDir = IO.Path.Combine(IO.Path.GetTempPath(), $"hermes-test-{Guid.NewGuid():N}")
    IO.Directory.CreateDirectory(archiveDir) |> ignore
    try
        ServiceHost.requestSync archiveDir
        let triggerPath = IO.Path.Combine(archiveDir, "hermes-sync-now")
        Assert.True(IO.File.Exists(triggerPath))
    finally
        try IO.Directory.Delete(archiveDir, true) with _ -> ()

// ─── Default service config ──────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_DefaultServiceConfig_HasSensibleDefaults`` () =
    let config = TestHelpers.testConfig "/test/archive"
    let sc = ServiceHost.defaultServiceConfig config
    Assert.True(sc.SyncIntervalMinutes > 0)
    Assert.True(sc.HeartbeatIntervalSeconds > 0)
    Assert.Equal("/test/archive", sc.ArchiveDir)
