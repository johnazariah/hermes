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

module RefusalEvidence =

    type private Expected =
        { Evidence: StartupRefusalEvidence
          Line: string }

    let private native =
        { Evidence =
            StartupRefusalEvidence(
                StartupEvidenceStage.ReservedRoot, StartupEvidenceOperation.OpenFollowing,
                StartupEvidenceFailure.NativeCallFailed, StartupEvidenceErrorKind.Win32, 5, -1)
          Line = "HERMES_STARTUP_EVIDENCE v=1 stage=2 operation=3 failure=2 errorKind=1 error=5 position=-1" }

    let private composition =
        { Evidence =
            StartupRefusalEvidence(
                StartupEvidenceStage.Composition, StartupEvidenceOperation.Compose,
                StartupEvidenceFailure.ManagedException, StartupEvidenceErrorKind.HResult, -2147024891, -1)
          Line = "HERMES_STARTUP_EVIDENCE v=1 stage=1 operation=1 failure=7 errorKind=2 error=-2147024891 position=-1" }

    let private execute readEvidence captured =
        Gate.runWithEvidence captured.Capability readEvidence
            (fun line -> captured.Events.Enqueue(Diagnostic line))
            captured.RunFactory [||]

    let private assertBounded (expected: Expected) =
        let line = expected.Evidence.ToDiagnosticLine()
        Assert.Equal(expected.Line, line)
        Assert.InRange(line.Length, 1, 192)

    let private verifyUnavailable evidence lines admission =
        let captured = capture true 61 admission
        let readEvidence () =
            Assert.Equal<Event list>(
                [ PlatformChecked true; Entered RuntimeEntry.Service; Diagnostic unavailable.Diagnostic ],
                captured.Events |> Seq.toList)
            evidence
        Assert.Equal(78, execute readEvidence captured)
        Assert.Equal<Event list>(
            [ PlatformChecked true; Entered RuntimeEntry.Service ] @ (lines |> List.map Diagnostic),
            captured.Events |> Seq.toList)

    let private unread () : (StartupRefusalEvidence | null) =
        failwith "Evidence must not be inspected for this outcome"

    let private verifyOtherRefusal expected admission =
        let captured = capture true 61 admission
        Assert.Equal(expected.ExitCode, execute unread captured)
        Assert.Equal<Event list>(
            [ PlatformChecked true; Entered RuntimeEntry.Service; Diagnostic expected.Diagnostic ],
            captured.Events |> Seq.toList)

    let private verifyPermitted isWindows admission =
        let captured = capture isWindows 61 admission
        Assert.Equal(61, execute unread captured)
        let expected =
            [ PlatformChecked isWindows ]
            @ (if isWindows then [ Entered RuntimeEntry.Service ] else [])
            @ [ FactoryCreated; ApplicationCalled [||] ]
        Assert.Equal<Event list>(expected, captured.Events |> Seq.toList)

    [<Fact>]
    [<Trait("Category", "Unit")>]
    let ``Unavailable writes its marker before exactly one bounded typed evidence line`` () =
        assertBounded native
        verifyUnavailable native.Evidence [ unavailable.Diagnostic; native.Line ]
            (fun () -> Refused(RefusalReason.Unavailable))

    [<Fact>]
    [<Trait("Category", "Unit")>]
    let ``A known composition exception retains captured evidence without its message`` () =
        assertBounded composition
        verifyUnavailable composition.Evidence [ unavailable.Diagnostic; composition.Line ]
            (fun () -> raise (UnauthorizedAccessException("synthetic private composition detail")))

    [<Fact>]
    [<Trait("Category", "Unit")>]
    let ``Null evidence leaves the unavailable diagnostic unchanged`` () =
        verifyUnavailable null [ unavailable.Diagnostic ]
            (fun () -> Refused(RefusalReason.Unavailable))

    [<Fact>]
    [<Trait("Category", "Unit")>]
    let ``Other refusals and invalid results never inspect or emit evidence`` () =
        rejections
        |> List.filter (fun expected -> expected.Reason <> RefusalReason.Unavailable)
        |> List.iter (fun expected -> verifyOtherRefusal expected (fun () -> Refused(expected.Reason)))
        verifyOtherRefusal invalid (fun () -> null)

    [<Fact>]
    [<Trait("Category", "Unit")>]
    let ``Permitted and nonWindows runs never inspect or emit evidence`` () =
        verifyPermitted true (fun () -> Admitted(Lease()))
        verifyPermitted true (fun () -> OutsideKnownInstallation())
        verifyPermitted false (fun () -> invalidOp "Windows admission must remain untouched")
