namespace Hermes.Tests

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Threading
open System.Threading.Tasks
open Xunit
open Xunit.Abstractions
open Hermes.Core
open Hermes.Core.LocalAccessWire
open Hermes.Core.OwnerControlPipe

module private OwnerPipeCrashFixture =
    type Root =
        { Directory: string }
        interface IDisposable with
            member root.Dispose() = Directory.Delete(root.Directory, true)

    type Child =
        { Process: Process
          Ready: Task<string | null>
          Join: unit -> Task }
        interface IAsyncDisposable with
            member child.DisposeAsync() =
                ValueTask(task {
                    try do! child.Join()
                    finally child.Process.Dispose()
                })

    let private helperBody = """
open System
open System.Globalization
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open Hermes.Core
open Hermes.Core.LocalAccessWire
open Hermes.Core.OwnerControlPipe
let target =
    OwnerControlTarget.tryCreate fsi.CommandLineArgs.[1] (Int32.Parse(fsi.CommandLineArgs.[2], CultureInfo.InvariantCulture))
    |> Result.defaultWith (fun _ -> failwith "Synthetic target rejected.")
let identity: Identity =
    { InstanceId = "AAAAAAAAAAAAAAAAAAAAAA"; ProcessId = 4123
      ProcessStartTimeUtc = DateTimeOffset(2026, 10, 19, 23, 59, 0, TimeSpan.Zero)
      Origin = target.ApiOrigin; ConfigKey = target.ConfigKey }
let dispatch (request: Request) : Task<Response> =
    let status: StatusResult = { Phase = Phase.Ready; Capabilities = [ "archive.read" ] }
    Task.FromResult({ RequestId = request.RequestId; Instance = identity; Outcome = Ok(Reply.Status status) }: Response)
let factory = Factory.create target dispatch ignore
let running = factory.Start CancellationToken.None
running.Ready.GetAwaiter().GetResult() |> Result.defaultWith (fun _ -> failwith "Synthetic listener failed.")
printfn "READY %s" RuntimeInformation.FrameworkDescription
Console.Out.Flush()
running.RunTask.GetAwaiter().GetResult() |> ignore
"""

    let root () =
        let directory = Path.Combine(Path.GetTempPath(), "hermes-owner-crash-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(directory) |> ignore
        { Directory = directory }

    let private writeScript (root: Root) =
        let reference = typeof<Request>.Assembly.Location.Replace("\"", "\"\"")
        let script = "#r @\"" + reference + "\"\n" + helperBody
        let path = Path.Combine(root.Directory, "owned-owner-pipe.fsx")
        File.WriteAllText(path, script, UTF8Encoding(false))
        path

    let private readOutput (child: Process) (ready: TaskCompletionSource<string | null>) =
        task {
            try
                let! line = child.StandardOutput.ReadLineAsync()
                ready.TrySetResult(line) |> ignore
                return! child.StandardOutput.ReadToEndAsync()
            with error ->
                ready.TrySetException(error) |> ignore
                return raise error
        }

    let private kill (child: Process) =
        try
            if not child.HasExited then child.Kill(true)
        with :? InvalidOperationException when child.HasExited -> ()

    let private join (child: Process) (output: Task<string>) (errors: Task<string>) () : Task =
        task {
            kill child
            do! child.WaitForExitAsync()
            let! _ = Task.WhenAll([| output; errors |])
            return ()
        }

    let private startInfo (root: Root) (target: OwnerControlTarget.Target) =
        let host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") |> Option.ofObj |> Option.defaultValue "dotnet"
        let info =
            ProcessStartInfo(FileName = host, WorkingDirectory = AppContext.BaseDirectory,
                             UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true)
        [ "fsi"; "--nologo"; "--exec"; writeScript root
          target.CanonicalDirectory; string target.Port ]
        |> List.iter info.ArgumentList.Add
        info

    let start root target =
        let child = new Process(StartInfo = startInfo root target)
        if not (child.Start()) then
            child.Dispose()
            failwith "Could not start the owned pipe helper."
        let ready = TaskCompletionSource<string | null>(TaskCreationOptions.RunContinuationsAsynchronously)
        let output = readOutput child ready
        let errors = child.StandardError.ReadToEndAsync()
        { Process = child; Ready = ready.Task; Join = join child output errors }

    let ready (output: ITestOutputHelper) (child: Child) =
        task {
            let! line = child.Ready.WaitAsync(TimeSpan.FromSeconds 30.0)
            match line with
            | null -> failwith "Owned pipe helper exited before readiness."
            | line ->
                Assert.StartsWith("READY .NET 9.", line)
                output.WriteLine(line)
        }

type OwnerControlPipeRestartTests(output: ITestOutputHelper) =
    [<Fact>]
    member _.``records the actual standard pipe runtime and platform`` () =
        output.WriteLine(RuntimeInformation.FrameworkDescription)
        output.WriteLine(RuntimeInformation.OSDescription)
        output.WriteLine("Runtime version: " + Environment.Version.ToString())
        Assert.Equal<int>(9, Environment.Version.Major)

    [<Fact>]
    member _.``owned helper crash permits same name restart using standard runtime recovery`` () =
        task {
            use root = OwnerPipeCrashFixture.root ()
            let target = OwnerControlTarget.tryCreate root.Directory 31991 |> OwnerPipeFixture.ok
            use child = OwnerPipeCrashFixture.start root target
            do! OwnerPipeCrashFixture.ready output child
            use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 10.0)
            let api = Factory.create target (fun request -> Task.FromResult(OwnerPipeFixture.response target request)) ignore
            let! first = api.Send (OwnerPipeFixture.request ()) (OwnerPipeFixture.peer target) deadline.Token
            Assert.True(Result.isOk first)
            do! child.Join()
            let rig = OwnerPipeFixture.start target (fun request _ -> Task.FromResult(OwnerPipeFixture.response target request))
            use run = rig.Run
            do! OwnerPipeFixture.ready rig
            do! OwnerPipeFixture.healthy rig deadline.Token
            do! OwnerPipeFixture.stopped run
        }
