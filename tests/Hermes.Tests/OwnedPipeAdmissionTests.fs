module Hermes.Tests.OwnedPipeAdmissionTests

open System
open System.IO
open Xunit
open Hermes.Tests.OwnedServiceProcess

let private onUnix verify =
    Assert.False(OperatingSystem.IsWindows(), "Unix-only test: on Windows use --filter \"Platform!=Unix\".")
    withRoot verify

[<Theory>]
[<InlineData("shutdown\n")>]
[<InlineData("")>]
[<Trait("Category", "Integration")>]
[<Trait("Platform", "Unix")>]
let ``Unix owned service rejects regular file stdin before archive startup`` (contents: string) =
    onUnix (fun root ->
        task {
            let paths = layout root
            let inputFile = Path.Combine(root, "synthetic-owner-input.txt")
            File.WriteAllText(inputFile, contents)
            use child = startWithFileInput paths inputFile true
            let! result = exited child
            Assert.NotEqual(0, result.Code)
            Assert.Contains("requires a redirected owner pipe", result.Output + result.Errors)
            Assert.False(Directory.Exists(paths.Archive), result.Output + result.Errors)
            Assert.DoesNotContain("Hermes service starting", result.Output)
        })

[<Theory>]
[<InlineData("shutdown\n")>]
[<InlineData("")>]
[<Trait("Category", "Integration")>]
[<Trait("Platform", "Unix")>]
let ``Unix foreground service does not interpret regular file stdin as owned control`` (contents: string) =
    onUnix (fun root ->
        task {
            let paths = layout root
            let inputFile = Path.Combine(root, "synthetic-foreground-input.txt")
            File.WriteAllText(inputFile, contents)
            use child = startWithFileInput paths inputFile false
            do! healthy child
            Assert.False(child.Process.HasExited)
            Assert.True(Directory.Exists(paths.Archive))
        })
