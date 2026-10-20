module Hermes.Tests.SyntheticChildProfileTests

open System.Diagnostics
open System.IO
open Xunit
open Hermes.Tests.CanonicalUiTestSupport

let private startInfo () =
    let info = ProcessStartInfo(UseShellExecute = false)
    info.Environment.Clear()
    [ "HOME"; "USERPROFILE"; "LOCALAPPDATA"; "APPDATA" ]
    |> List.iter (fun name -> info.Environment.[name] <- "synthetic-inherited-profile")
    info

let private assertProfile home (info: ProcessStartInfo) =
    let appData = Path.Combine(home, "AppData")
    [ "HOME", home
      "USERPROFILE", home
      "LOCALAPPDATA", Path.Combine(appData, "Local")
      "APPDATA", Path.Combine(appData, "Roaming") ]
    |> List.iter (fun (name, path) ->
        Assert.Equal(path, info.Environment.[name])
        Assert.True(Path.IsPathFullyQualified(path))
        Assert.True(Directory.Exists(path)))

[<Fact>]
let ``Synthetic child profiles materialise native known-folder directories`` () =
    withDirectory (fun root ->
        task {
            let home = Path.Combine(root, "synthetic-home")
            let info = startInfo ()
            Assert.False(Directory.Exists(home))
            SyntheticChildProfile.configure info home
            assertProfile home info
        })

[<Fact>]
let ``Repeated profile setup preserves owned files and unrelated child settings`` () =
    withDirectory (fun root ->
        task {
            let home = Path.Combine(root, "synthetic-home")
            let config = Path.Combine(root, "synthetic-config")
            let info = startInfo ()
            info.Environment.["HERMES_CONFIG_DIR"] <- config
            SyntheticChildProfile.configure info home
            let marker = Path.Combine(home, "AppData", "Local", "synthetic-profile.txt")
            File.WriteAllText(marker, "synthetic")
            SyntheticChildProfile.configure info home
            assertProfile home info
            Assert.Equal(config, info.Environment.["HERMES_CONFIG_DIR"])
            Assert.Equal("synthetic", File.ReadAllText(marker))
            Assert.False(Directory.Exists(config))
        })

[<Fact>]
let ``Configuring another child keeps both synthetic profiles isolated`` () =
    withDirectory (fun root ->
        task {
            let firstHome = Path.Combine(root, "first")
            let secondHome = Path.Combine(root, "second")
            let first = startInfo ()
            let second = startInfo ()
            SyntheticChildProfile.configure first firstHome
            SyntheticChildProfile.configure second secondHome
            let marker = "synthetic-profile.txt"
            File.WriteAllText(Path.Combine(firstHome, "AppData", "Local", marker), "synthetic")
            assertProfile firstHome first
            assertProfile secondHome second
            Assert.False(File.Exists(Path.Combine(secondHome, "AppData", "Local", marker)))
        })
