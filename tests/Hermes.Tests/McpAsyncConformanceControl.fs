namespace Hermes.Tests

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.AsyncCapabilityControl
open Hermes.Tests.McpAsyncConformanceFixture

module McpAsyncConformanceControl =
    type Pause = Open | Read of Gate | Write of Gate | Ownership of Gate | Transaction of Gate

    let private unexpected (name: string) : 'T = invalidOp $"Unexpected MCP capability: {name}"

    let private selected pause (kind: string) (sql: string) =
        match pause, kind with
        | Read gate, "db.read" | Write gate, "db.write" -> Some gate
        | Ownership gate, "db.read" when sql.Contains("hermes_saved_path_key(saved_path)", StringComparison.Ordinal) ->
            Some gate
        | _ -> None

    let private invoke (trace: ConcurrentQueue<string>) pause db kind sql effect =
        trace.Enqueue(kind)
        selected pause kind sql
        |> Option.map (fun gate -> gate.Invoke db effect)
        |> Option.defaultWith effect

    let rec database (trace: ConcurrentQueue<string>) pause (source: Algebra.Database) : Algebra.Database =
        { source with
            execReader = fun sql values -> invoke trace pause source "db.read" sql (fun () -> source.execReader sql values)
            execScalar = fun sql values -> invoke trace pause source "db.scalar" sql (fun () -> source.execScalar sql values)
            execNonQuery = fun sql values -> invoke trace pause source "db.write" sql (fun () -> source.execNonQuery sql values)
            transaction = transaction trace pause source
            inTransaction = fun callback -> TransactionExecution.run (transaction trace pause source) callback
            initSchema = fun () -> unexpected "initSchema"
            schemaVersion = fun () -> unexpected "schemaVersion"
            tableExists = fun _ -> unexpected "tableExists"
            dispose = fun () -> unexpected "dispose" }

    and private transaction trace pause (source: Algebra.Database) : Algebra.Transaction =
        { new Algebra.Transaction with
            member _.Run<'T>(work: Algebra.Database -> Task<'T>) =
                task {
                    trace.Enqueue("begin")
                    let! result = source.transaction.Run(fun scoped ->
                        match pause with
                        | Transaction gate -> gate.Invoke scoped (fun () -> work (database trace Open scoped))
                        | _ -> work (database trace pause scoped))
                    trace.Enqueue("end")
                    return result
                } }

    let files (trace: ConcurrentQueue<string>) (gate: Gate option) (fixture: Fixture) : Algebra.FileSystem =
        let sourceFs = fixture.Files.Fs
        { sourceFs with
            fileExists = fun path -> trace.Enqueue("fs.exists"); sourceFs.fileExists path
            getFileSize = fun path -> trace.Enqueue("fs.size"); sourceFs.getFileSize path
            readAllText = fun path ->
                trace.Enqueue("fs.read")
                gate |> Option.map (fun control -> control.Invoke fixture.Db (fun () -> sourceFs.readAllText path))
                |> Option.defaultWith (fun () -> sourceFs.readAllText path)
            writeAllBytes = fun path bytes ->
                trace.Enqueue("fs.bytes")
                Assert.Equal(fixture.Files.Norm sidecar, fixture.Files.Norm path)
                sourceFs.writeAllBytes path bytes
            writeAllText = fun _ _ -> unexpected "writeAllText"
            readAllBytes = fun _ -> unexpected "readAllBytes"
            directoryExists = fun _ -> unexpected "directoryExists"
            createDirectory = fun _ -> unexpected "createDirectory"
            deleteFile = fun _ -> unexpected "deleteFile"
            moveFile = fun _ _ -> unexpected "moveFile"
            getFiles = fun _ _ -> unexpected "getFiles"
            getDirectories = fun _ -> unexpected "getDirectories" }

    let checkpoint (fixture: Fixture) before (gate: Gate) (pending: Task<'T>) =
        task {
            let! scoped = gate.Await(pending :> Task)
            Assert.False(gate.EffectStarted)
            let! actual = snapshot scoped fixture.Files
            Assert.Equal<Snapshot>(before, actual)
            return scoped
        }

    let resume fixture before (trace: ConcurrentQueue<string>) expected gate pending =
        task {
            let! _ = checkpoint fixture before gate pending
            Assert.Equal<string list>(expected, List.ofSeq trace)
            gate.Release()
        }

    let resumeOwned fixture before (trace: ConcurrentQueue<string>) expected gate pending =
        task {
            let! scoped = checkpoint fixture before gate pending
            Assert.Throws<InvalidOperationException>(fun () -> scoped.dispose()) |> ignore
            Assert.Equal<string list>(expected, List.ofSeq trace)
            gate.Release()
        }
