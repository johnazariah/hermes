namespace Hermes.Service

open System
open System.IO
open Hermes.Servicing

/// Admission surrounds every service-binary entry path, including early commands.
[<RequireQualifiedAccess>]
module StartupAdmission =

    type Capability =
        { IsWindows: unit -> bool
          GateEnterRole: RuntimeEntry -> (Admission | null) }

    type RunFactory = unit -> (string array -> int)

    type private Permission =
        | Outside
        | Installed of IProcessLease

    type private Failure =
        { ExitCode: int
          Diagnostic: string }

    let private refusal = function
        | RefusalReason.Busy -> { ExitCode = 75; Diagnostic = "HERMES_STARTUP_BUSY" }
        | RefusalReason.Closed -> { ExitCode = 78; Diagnostic = "HERMES_STARTUP_CLOSED" }
        | RefusalReason.UnownedInstallation -> { ExitCode = 78; Diagnostic = "HERMES_STARTUP_UNOWNED_INSTALLATION" }
        | RefusalReason.UnsupportedProtocol -> { ExitCode = 78; Diagnostic = "HERMES_STARTUP_UNSUPPORTED_PROTOCOL" }
        | RefusalReason.WrongGeneration -> { ExitCode = 78; Diagnostic = "HERMES_STARTUP_WRONG_GENERATION" }
        | RefusalReason.Unavailable -> { ExitCode = 78; Diagnostic = "HERMES_STARTUP_UNAVAILABLE" }
        | _ -> { ExitCode = 78; Diagnostic = "HERMES_STARTUP_INVALID_STATE" }

    let private readLease (lease: IProcessLease | null) =
        match lease with
        | null -> Error RefusalReason.InvalidState
        | lease -> Ok(Installed lease)

    let private interpret (admission: Admission | null) =
        match admission with
        | :? Admitted as admitted -> readLease admitted.Lease
        | :? OutsideKnownInstallation -> Ok Outside
        | :? Refused as refused -> Error refused.Reason
        | _ -> Error RefusalReason.InvalidState

    let private enter capability =
        try
            capability.GateEnterRole RuntimeEntry.Service |> interpret
        with
        | :? IOException
        | :? UnauthorizedAccessException
        | :? InvalidOperationException
        | :? ArgumentException
        | :? NotSupportedException -> Error RefusalReason.Unavailable

    let private runPermitted permission runFactory arguments =
        match permission with
        | Outside -> runFactory () arguments
        | Installed lease ->
            // The library, not this token, roots the kernel handle until process death.
            try runFactory () arguments
            finally GC.KeepAlive(lease)

    let private reportRefusal (readEvidence: unit -> (StartupRefusalEvidence | null)) writeRefusal reason =
        let failure = refusal reason
        writeRefusal failure.Diagnostic
        if reason = RefusalReason.Unavailable then
            readEvidence ()
            |> Option.ofObj
            |> Option.iter (fun evidence -> evidence.ToDiagnosticLine() |> writeRefusal)
        failure.ExitCode

    let internal runWithEvidence capability readEvidence writeRefusal (runFactory: RunFactory) (arguments: string array) : int =
        if capability.IsWindows () then
            enter capability
            |> Result.map (fun permission -> runPermitted permission runFactory arguments)
            |> Result.defaultWith (reportRefusal readEvidence writeRefusal)
        else
            runFactory () arguments

    let runWith capability writeRefusal (runFactory: RunFactory) (arguments: string array) : int =
        runWithEvidence capability (fun () -> null) writeRefusal runFactory arguments

    let run (runFactory: RunFactory) arguments =
        let evidence = StartupRefusalEvidenceCapture()
        let capability =
            { IsWindows = OperatingSystem.IsWindows
              GateEnterRole = fun role -> WindowsStartupAdmission.ForCurrentUser(evidence).Enter(role) }
        runWithEvidence capability (fun () -> evidence.Evidence)
            (fun code -> Console.Error.WriteLine(code: string)) runFactory arguments
