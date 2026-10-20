module Hermes.Tests.CanonicalUiStartupTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open Xunit
open Hermes.Service
open Hermes.Tests.CanonicalUiTestSupport

type private AssemblyMarker = AssemblyMarker

type private Layout =
    { Deployed: string
      Working: string
      Config: string
      Archive: string }

type private ChildResult =
    { ExitCode: int
      Output: string
      Error: string }

let private testAssembly = typeof<AssemblyMarker>.Assembly.Location

let private companion extension =
    Path.ChangeExtension(testAssembly, extension)
    |> Option.ofObj
    |> Option.defaultWith (fun () -> invalidOp "Missing test assembly companion path")

let private copyAssemblies deployed =
    Directory.CreateDirectory(deployed) |> ignore
    let directory =
        Path.GetDirectoryName(testAssembly)
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "Missing test assembly directory")
    Directory.GetFiles(directory, "*.dll")
    |> Array.append [| companion "runtimeconfig.json"; companion "deps.json" |]
    |> Array.iter (fun source ->
        let name =
            Path.GetFileName(source)
            |> Option.ofObj
            |> Option.defaultWith (fun () -> invalidOp "Missing deployed assembly filename")
        File.Copy(source, Path.Combine(deployed, name)))

let private layout root =
    let result =
        { Deployed = Path.Combine(root, "deployed")
          Working = Path.Combine(root, "unrelated-working-directory")
          Config = Path.Combine(root, "synthetic-config")
          Archive = Path.Combine(root, "archive-must-not-be-created") }
    copyAssemblies result.Deployed
    create result.Working |> ignore
    create (Path.Combine(result.Working, "src", "Hermes.Service")) |> ignore
    Directory.CreateDirectory(result.Config) |> ignore
    let archive = JsonSerializer.Serialize(result.Archive)
    let yaml = $"archive_dir: {archive}\naccounts: []\nwatch_folders: []\nollama:\n  enabled: false\n"
    File.WriteAllText(Path.Combine(result.Config, "config.yaml"), yaml)
    result

let private childEnvironment (info: ProcessStartInfo) paths =
    info.Environment.["HERMES_CONFIG_DIR"] <- paths.Config
    info.Environment.["HERMES_PORT"] <- "21741"
    info.Environment.["HERMES_ALLOWED_ORIGINS"] <- ""
    info.Environment.["HERMES_MCP_MUTATIONS"] <- "false"
    info.Environment.Remove("HERMES_MCP_TOKEN") |> ignore
    SyntheticChildProfile.configure info paths.Working
    info.Environment.["DOTNET_DbgEnableMiniDump"] <- "0"

let private startInfo paths =
    let host =
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
        |> Option.ofObj |> Option.defaultValue "dotnet"
    let info =
        ProcessStartInfo(
            FileName = host, WorkingDirectory = paths.Working,
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true)
    [ "exec"; "--runtimeconfig"; Path.Combine(paths.Deployed, "Hermes.Tests.runtimeconfig.json")
      "--depsfile"; Path.Combine(paths.Deployed, "Hermes.Tests.deps.json")
      Path.Combine(paths.Deployed, "Hermes.Service.dll") ]
    |> List.iter info.ArgumentList.Add
    childEnvironment info paths
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
            return { ExitCode = child.ExitCode; Output = stdout; Error = stderr }
        finally
            if not child.HasExited then child.Kill(true)
    }

let private run paths =
    task {
        use child = new Process(StartInfo = startInfo paths)
        if not (child.Start()) then invalidOp "Could not start synthetic service deployment"
        return! readResult child
    }

let private assertDeployedRoot (paths: Layout) (child: ChildResult) =
    let expected = Path.Combine(paths.Deployed, "wwwroot")
    let matched = Regex.Match(child.Error, @"Canonical React UI unavailable at '(?<root>[^\r\n]+?)': ")
    let evidence =
        $"Expected deployed root: {expected}\nChild exit: {child.ExitCode}\nChild stdout:\n{child.Output}\nChild stderr:\n{child.Error}"
    Assert.True(matched.Success, evidence)
    let reported = matched.Groups.["root"].Value
    Assert.True(Path.IsPathFullyQualified(reported), evidence)
    Assert.True(String.Equals(Path.GetFileName(reported), "wwwroot", StringComparison.Ordinal), evidence)
    let parent =
        Path.GetDirectoryName(reported)
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp evidence)
    let nonce = Guid.NewGuid().ToString("N") + ".deployment-identity"
    // Created after child exit: an alias must expose this same newly created owned file.
    File.WriteAllText(Path.Combine(paths.Deployed, nonce), nonce)
    let probe = Path.Combine(parent, nonce)
    Assert.True(File.Exists(probe), evidence)
    Assert.True(File.ReadAllText(probe) = nonce, evidence)

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Actual service fails before archive startup despite valid CWD and source decoy bundles`` () =
    withDirectory (fun root ->
        task {
            let paths = layout root
            let! child = run paths
            Assert.NotEqual(0, child.ExitCode)
            Assert.Contains("Canonical React UI unavailable", child.Error)
            Assert.False(Directory.Exists(paths.Archive), child.Error)
            Assert.DoesNotContain("Hermes service starting", child.Output)
            Assert.Contains("npm run build --prefix src/Hermes.Web", child.Error)
            assertDeployedRoot paths child
        })

[<Theory>]
[<InlineData("""<script type='module' src='/src/main.tsx'></script>""")>]
[<InlineData("""<script src='assets/missing.js' type='module'></script>""")>]
[<Trait("Category", "Integration")>]
let ``Actual service rejects mixed module entries before creating its archive`` extra =
    withDirectory (fun root ->
        task {
            let paths = layout root
            let fixture = create paths.Deployed
            write fixture indexName (index.Replace("</body>", extra + "</body>"))
            let! child = run paths
            Assert.NotEqual(0, child.ExitCode)
            Assert.Contains("Invalid index.html", child.Error)
            Assert.False(Directory.Exists(paths.Archive), child.Error)
            Assert.DoesNotContain("Hermes service starting", child.Output)
            assertDeployedRoot paths child
        })
