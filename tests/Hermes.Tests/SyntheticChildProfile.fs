module Hermes.Tests.SyntheticChildProfile

open System.Diagnostics
open System.IO

/// Native known-folder lookup verifies that the synthetic directories exist.
let configure (info: ProcessStartInfo) home =
    let appData = Path.Combine(home, "AppData")
    let local = Path.Combine(appData, "Local")
    let roaming = Path.Combine(appData, "Roaming")
    [ local; roaming ]
    |> List.iter (Directory.CreateDirectory >> ignore)
    [ "HOME", home
      "USERPROFILE", home
      "LOCALAPPDATA", local
      "APPDATA", roaming ]
    |> List.iter (fun (name, value) -> info.Environment.[name] <- value)
