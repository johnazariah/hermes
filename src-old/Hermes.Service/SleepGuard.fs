namespace Hermes.Service

open System.Runtime.InteropServices

/// Prevents Windows from sleeping while the pipeline is processing.
/// Uses SetThreadExecutionState to signal continuous operation.
[<RequireQualifiedAccess>]
module SleepGuard =

    [<Literal>]
    let private ES_CONTINUOUS = 0x80000000u
    [<Literal>]
    let private ES_SYSTEM_REQUIRED = 0x00000001u
    [<Literal>]
    let private ES_AWAYMODE_REQUIRED = 0x00000040u

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern uint32 SetThreadExecutionState(uint32 esFlags)

    /// Prevent sleep — call when pipeline has work.
    let preventSleep () =
        SetThreadExecutionState(ES_CONTINUOUS ||| ES_SYSTEM_REQUIRED ||| ES_AWAYMODE_REQUIRED) |> ignore

    /// Allow sleep — call when pipeline is idle.
    let allowSleep () =
        SetThreadExecutionState(ES_CONTINUOUS) |> ignore
