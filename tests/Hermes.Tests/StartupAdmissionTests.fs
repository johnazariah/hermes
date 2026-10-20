module Hermes.Tests.StartupAdmissionTests

open System
open System.Collections.Concurrent
open System.IO
open Xunit
open Hermes.Servicing

module Gate = Hermes.Service.StartupAdmission

type private Lease() =
    interface IProcessLease

type private Event =
    | PlatformChecked of bool
    | Entered of RuntimeEntry
    | FactoryCreated
    | ApplicationCalled of string array
    | Diagnostic of string

type private Captured =
    { Capability: Gate.Capability
      RunFactory: Gate.RunFactory
      Events: ConcurrentQueue<Event> }

type private Rejection =
    { Reason: RefusalReason
      ExitCode: int
      Diagnostic: string }

let private unavailable =
    { Reason = RefusalReason.Unavailable; ExitCode = 78; Diagnostic = "HERMES_STARTUP_UNAVAILABLE" }

let private invalid =
    { Reason = RefusalReason.InvalidState; ExitCode = 78; Diagnostic = "HERMES_STARTUP_INVALID_STATE" }

let private rejections =
    [ { Reason = RefusalReason.Busy; ExitCode = 75; Diagnostic = "HERMES_STARTUP_BUSY" }
      { Reason = RefusalReason.Closed; ExitCode = 78; Diagnostic = "HERMES_STARTUP_CLOSED" }
      { Reason = RefusalReason.UnownedInstallation; ExitCode = 78; Diagnostic = "HERMES_STARTUP_UNOWNED_INSTALLATION" }
      { Reason = RefusalReason.UnsupportedProtocol; ExitCode = 78; Diagnostic = "HERMES_STARTUP_UNSUPPORTED_PROTOCOL" }
      { Reason = RefusalReason.WrongGeneration; ExitCode = 78; Diagnostic = "HERMES_STARTUP_WRONG_GENERATION" }
      invalid
      unavailable
      { invalid with Reason = enum<RefusalReason> 999 } ]

let private invocations =
    [ ""
      "--initial-sync-days 3"
      "--control-stdin"
      "--control-stdin --initial-sync-days 3"
      "--control-stdin=true"
      "--control-stdin --control-stdin"
      "reindex-files --document-id 1"
      "reindex-files --apply --document-id 1 --config forbidden.yaml"
      "reindex-files --dry-run --document-id 1"
      "--control-stdin reindex-files --document-id 1"
      "reindex-file"
      "--document-id 1"
      "owner future-command"
      "--unknown-argument" ]
    |> List.map (fun text -> text.Split(' ', StringSplitOptions.RemoveEmptyEntries))

let private capture isWindows exitCode (admission: unit -> (Admission | null)) =
    let events = ConcurrentQueue<Event>()
    { Capability =
        { IsWindows = fun () ->
            events.Enqueue(PlatformChecked isWindows)
            isWindows
          GateEnterRole = fun role ->
            events.Enqueue(Entered role)
            admission () }
      RunFactory = fun () ->
        events.Enqueue FactoryCreated
        fun arguments ->
            events.Enqueue(ApplicationCalled arguments)
            exitCode
      Events = events }

let private execute captured arguments =
    Gate.runWith captured.Capability
        (fun code -> captured.Events.Enqueue(Diagnostic code))
        captured.RunFactory arguments

let private assertRefused expected arguments captured =
    Assert.Equal(expected.ExitCode, execute captured arguments)
    let events = captured.Events |> Seq.toList
    Assert.Equal<Event list>(
        [ PlatformChecked true; Entered RuntimeEntry.Service; Diagnostic expected.Diagnostic ], events)
    Assert.Equal(0, events |> List.filter ((=) FactoryCreated) |> List.length)

let private verifyRefused expected arguments =
    capture true 61 (fun () -> Refused(expected.Reason))
    |> assertRefused expected arguments

let private verifyRun isWindows admission exitCode arguments =
    let captured = capture isWindows exitCode admission
    Assert.Equal(exitCode, execute captured arguments)
    let expected =
        [ PlatformChecked isWindows ]
        @ (if isWindows then [ Entered RuntimeEntry.Service ] else [])
        @ [ FactoryCreated; ApplicationCalled arguments ]
    Assert.Equal<Event list>(expected, captured.Events |> Seq.toList)
    captured.Events
    |> Seq.choose (function ApplicationCalled actual -> Some actual | _ -> None)
    |> Seq.exactlyOne
    |> fun actual -> Assert.Same(arguments, actual)

let private verifyRuns isWindows admission =
    [ 0; 1; 61; 75; 78; -17 ]
    |> List.iter (fun exitCode -> invocations |> List.iter (verifyRun isWindows admission exitCode))

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Every argv shape is refused before constructing any application capability`` () =
    rejections
    |> List.iter (fun expected -> invocations |> List.iter (verifyRefused expected))

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Installed admission uses Service and runs the factory exactly once`` () =
    verifyRuns true (fun () -> Admitted(Lease()))

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Verified outside admission runs without constructing a process lease`` () =
    verifyRuns true (fun () -> OutsideKnownInstallation())

[<Fact>]
[<Trait("Category", "Unit")>]
let ``NonWindows never invokes the Windows admission factory`` () =
    verifyRuns false (fun () -> invalidOp "Windows admission factory must remain untouched")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``An unexpected admission result fails closed for every argv shape`` () =
    invocations
    |> List.iter (fun arguments ->
        capture true 61 (fun () -> null)
        |> assertRefused invalid arguments)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Known admission failures emit only the stable unavailable diagnostic`` () =
    let detail = "private admission detail must not be logged"
    let failures: exn list =
        [ IOException(detail); UnauthorizedAccessException(detail)
          InvalidOperationException(detail); ArgumentException(detail); NotSupportedException(detail) ]
    failures
    |> List.iter (fun error ->
        invocations
        |> List.iter (fun arguments ->
            capture true 61 (fun () -> raise error)
            |> assertRefused unavailable arguments))

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Application failures are not relabelled as admission failures`` () =
    let captured = capture true 0 (fun () -> Admitted(Lease()))
    let factory () =
        captured.Events.Enqueue FactoryCreated
        fun arguments ->
            captured.Events.Enqueue(ApplicationCalled arguments)
            raise (IOException("application failure"))
    let changed = { captured with RunFactory = factory }
    Assert.Throws<IOException>(fun () -> execute changed [||] |> ignore) |> ignore
    Assert.Equal<Event list>(
        [ PlatformChecked true; Entered RuntimeEntry.Service; FactoryCreated; ApplicationCalled [||] ],
        captured.Events |> Seq.toList)
