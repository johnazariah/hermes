module Hermes.Tests.OwnedServiceProcess

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Xunit

type Paths =
    { Root: string
      Config: string
      Archive: string
      Port: int }

type Exit =
    { Code: int
      Output: string
      Errors: string }

type Child =
    { Process: Process
      Output: Task<string>
      Errors: Task<string>
      Client: HttpClient }
    interface IDisposable with
        member child.Dispose() =
            child.Client.Dispose()
            if not child.Process.HasExited then child.Process.Kill(true)
            if not (child.Process.WaitForExit(5000)) then
                invalidOp "Synthetic owned service did not exit after cleanup kill"
            Task.WhenAll([| child.Output; child.Errors |])
                .WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
            |> ignore
            child.Process.Dispose()

let private parent (directory: DirectoryInfo) =
    directory.Parent
    |> Option.ofObj
    |> Option.defaultWith (fun () -> invalidOp "Could not locate repository/build output")

let rec private repository (directory: DirectoryInfo) =
    let project = Path.Combine(directory.FullName, "src", "Hermes.Service", "Hermes.Service.fsproj")
    if File.Exists(project) then directory.FullName
    else repository (parent directory)

let private assembly () =
    let output = DirectoryInfo(AppContext.BaseDirectory)
    let configuration = (parent output).Name
    let path =
        Path.Combine(
            repository output, "src", "Hermes.Service", "bin",
            configuration, output.Name, "Hermes.Service.dll")
    Assert.True(File.Exists(path), $"Actual Service build output is missing: {path}")
    path

let private freePort () =
    use listener = new TcpListener(IPAddress.Loopback, 0)
    listener.Start()
    (listener.LocalEndpoint :?> IPEndPoint).Port

let layout root =
    let paths =
        { Root = root; Config = Path.Combine(root, "config")
          Archive = Path.Combine(root, "archive"); Port = freePort () }
    Directory.CreateDirectory(paths.Config) |> ignore
    let archive = JsonSerializer.Serialize(paths.Archive)
    let yaml =
        $"archive_dir: {archive}\naccounts: []\nwatch_folders: []\nsync_interval_minutes: 60\nollama:\n  enabled: false\n"
    File.WriteAllText(Path.Combine(paths.Config, "config.yaml"), yaml)
    paths

let withRoot verify =
    task {
        let root = Path.Combine(Path.GetTempPath(), "hermes-owned-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        try do! verify root
        finally Directory.Delete(root, true)
    }

let private environment (info: ProcessStartInfo) paths =
    info.Environment.["HERMES_CONFIG_DIR"] <- paths.Config
    info.Environment.["HERMES_PORT"] <- string paths.Port
    info.Environment.["HERMES_ALLOWED_ORIGINS"] <- ""
    info.Environment.["HERMES_MCP_MUTATIONS"] <- "false"
    SyntheticChildProfile.configure info paths.Root
    info.Environment.["DOTNET_DbgEnableMiniDump"] <- "0"
    [ "HERMES_MCP_TOKEN"; "HERMES_GOOGLE_CREDENTIALS"; "HERMES_GOOGLE_CREDENTIALS_FILE" ]
    |> List.iter (fun name -> info.Environment.Remove(name) |> ignore)

let private startInfo paths =
    let host =
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
        |> Option.ofObj |> Option.defaultValue "dotnet"
    let info =
        ProcessStartInfo(
            FileName = host, WorkingDirectory = paths.Root, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true)
    [ assembly (); "--control-stdin" ] |> List.iter info.ArgumentList.Add
    environment info paths
    info

let private launch paths info =
    let child = new Process(StartInfo = info)
    if not (child.Start()) then
        child.Dispose()
        invalidOp "Could not start synthetic owned service"
    { Process = child
      Output = child.StandardOutput.ReadToEndAsync()
      Errors = child.StandardError.ReadToEndAsync()
      Client =
        new HttpClient(
            BaseAddress = Uri($"http://127.0.0.1:{paths.Port}"),
            Timeout = TimeSpan.FromSeconds 2.0) }

let start paths = launch paths (startInfo paths)

/// Only the isolated child shell changes its stdin descriptor.
let startWithFileInput paths inputFile owned =
    let info = startInfo paths
    let host = info.FileName
    let arguments =
        info.ArgumentList
        |> Seq.filter (fun argument ->
            owned || argument <> Hermes.Service.OwnedControl.Flag)
        |> Seq.toList
    info.FileName <- "/bin/sh"
    info.RedirectStandardInput <- false
    info.ArgumentList.Clear()
    [ "-c"; """input=$1; shift; exec "$@" < "$input" """
      "hermes-isolated-file-input"; inputFile; host ]
    @ arguments
    |> List.iter info.ArgumentList.Add
    launch paths info

module private StartupHealth =
    let private earlyExitMessage =
        "Owned service exited before its identity health check"

    let private redact (text: string) =
        let authority =
            @"(?i:\bBearer[ \t]+[^\s""'<>]+)"
            + @"|h1\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+"
            + @"|(?<![A-Za-z0-9_-])[A-Za-z0-9_-]{43}(?![A-Za-z0-9_-])"
            + @"|(?<![A-Za-z0-9_-])[A-Za-z0-9]{4}(?:-[A-Za-z0-9]{4}){2}(?![A-Za-z0-9_-])"
        Regex.Replace(text, authority, "[redacted]", RegexOptions.CultureInvariant)

    let private bounded limit (text: string) =
        let marker = "... [truncated]"
        if text.Length <= limit then text
        else text.Substring(0, limit - marker.Length) + marker

    let diagnostic (result: Exit) =
        let heading = $"{earlyExitMessage}; exit code {result.Code}."
        let stdoutHeading = "\nChild stdout:\n"
        let stderrHeading = "\nChild stderr:\n"
        let overhead = heading.Length + stdoutHeading.Length + stderrHeading.Length
        let streamLimit = (4096 - overhead) / 2
        // Redact complete captures before bounding: never expose a cut authority value.
        let output = result.Output |> redact |> bounded streamLimit
        let errors = result.Errors |> redact |> bounded streamLimit
        String.concat "" [ heading; stdoutHeading; output; stderrHeading; errors ]

    let private captureExit (child: Child) (token: CancellationToken) =
        task {
            let! output = child.Output.WaitAsync(token)
            let! errors = child.Errors.WaitAsync(token)
            return diagnostic { Code = child.Process.ExitCode; Output = output; Errors = errors }
        }

    let private assertHealth processId (health: JsonElement) =
        Assert.Equal("healthy", health.GetProperty("status").GetString())
        Assert.Equal("hermes", health.GetProperty("service").GetString())
        Assert.Equal(processId, health.GetProperty("processId").GetInt32())

    let checkHealth (client: HttpClient) processId (token: CancellationToken) =
        task {
            try
                use! response = client.GetAsync("/health", token)
                if int response.StatusCode <> 200 then return false
                else
                    let! text = response.Content.ReadAsStringAsync(token)
                    use document = JsonDocument.Parse(text)
                    assertHealth processId document.RootElement
                    return true
            with
            | :? HttpRequestException when not token.IsCancellationRequested -> return false
            | :? TaskCanceledException when not token.IsCancellationRequested -> return false
        }

    let rec private waitForHealthWith
        (onEarlyExit: CancellationToken -> Task<string>)
        client processId hasExited (token: CancellationToken) =
        task {
            token.ThrowIfCancellationRequested()
            if hasExited () then
                let! message = onEarlyExit token
                invalidOp message
            let! ready = checkHealth client processId token
            token.ThrowIfCancellationRequested()
            if not ready then
                do! Task.Delay(25, token)
                return! waitForHealthWith onEarlyExit client processId hasExited token
        }

    let waitForHealth client processId hasExited token =
        waitForHealthWith (fun _ -> Task.FromResult earlyExitMessage)
            client processId hasExited token

    let waitForChild (child: Child) token =
        waitForHealthWith (captureExit child)
            child.Client child.Process.Id (fun () -> child.Process.HasExited) token

let internal earlyExitDiagnostic = StartupHealth.diagnostic
let internal checkHealth = StartupHealth.checkHealth
let internal waitForHealth = StartupHealth.waitForHealth

let healthy (child: Child) =
    task {
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 20.0)
        return! StartupHealth.waitForChild child deadline.Token
    }

let exited child =
    task {
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 70.0)
        do! child.Process.WaitForExitAsync(deadline.Token)
        let! output = child.Output.WaitAsync(deadline.Token)
        let! errors = child.Errors.WaitAsync(deadline.Token)
        return { Code = child.Process.ExitCode; Output = output; Errors = errors }
    }

let stop child command =
    task {
        if command = "eof" then child.Process.StandardInput.Close()
        else
            do! child.Process.StandardInput.WriteLineAsync(command: string)
            do! child.Process.StandardInput.FlushAsync()
        let! result = exited child
        Assert.True(result.Code = 0, $"Exit {result.Code}\n{result.Output}\n{result.Errors}")
        return result
    }
