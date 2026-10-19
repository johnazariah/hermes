module Hermes.Tests.LocalReindexProcess

open System
open System.Diagnostics
open System.IO
open System.Threading
open Hermes.Service
open Hermes.Tests.LocalReindexFixtures

type private AssemblyMarker = AssemblyMarker

type Result =
    { ExitCode: int
      StandardOutput: string
      StandardError: string }

let private startInfo (fixture: Fixture) (arguments: string array) =
    let testAssembly = typeof<AssemblyMarker>.Assembly.Location
    let serviceAssembly = typeof<ReindexCommand.Request>.Assembly.Location
    let host =
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
        |> Option.ofObj |> Option.defaultValue "dotnet"
    let info =
        ProcessStartInfo(
            FileName = host, WorkingDirectory = fixture.Root,
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true)
    let companion extension =
        Path.ChangeExtension(testAssembly, extension)
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "Test assembly companion path could not be derived")
    [ "exec"; "--runtimeconfig"; companion "runtimeconfig.json"
      "--depsfile"; companion "deps.json"; serviceAssembly ]
    @ Array.toList arguments
    |> List.iter info.ArgumentList.Add
    info.Environment.["HERMES_CONFIG_DIR"] <- Path.Combine(fixture.Root, "unused-config")
    info.Environment.["HERMES_PORT"] <- "not-a-service-port"
    info

let private readResult (child: Process) =
    task {
        use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 20.0)
        let output = child.StandardOutput.ReadToEndAsync()
        let errors = child.StandardError.ReadToEndAsync()
        try
            do! child.WaitForExitAsync(timeout.Token)
            let! stdout = output
            let! stderr = errors
            return { ExitCode = child.ExitCode; StandardOutput = stdout; StandardError = stderr }
        finally
            if not child.HasExited then child.Kill(true)
    }

let run fixture arguments =
    task {
        use child = new Process(StartInfo = startInfo fixture arguments)
        if not (child.Start()) then invalidOp "Could not start the synthetic Hermes CLI"
        return! readResult child
    }
