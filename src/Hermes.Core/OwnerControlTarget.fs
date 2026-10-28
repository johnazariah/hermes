namespace Hermes.Core

open System
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text

[<RequireQualifiedAccess>]
module OwnerControlTarget =

    [<RequireQualifiedAccess>]
    type Failure =
        | InvalidDirectory
        | InvalidPort

    // Avoid expression-name resolution against FSharp.Core's Failure constructor.
    // This abbreviation does not change the public Failure type.
    type private TargetFailure = Failure

    type Target =
        { CanonicalDirectory: string
          Port: int
          ConfigKey: string
          PipeName: string
          ApiOrigin: string }

    let private validPort (port: int) = port >= 1 && port <= 65535

    let rec private trimSeparators (path: string) : string =
        let trimmed = Path.TrimEndingDirectorySeparator path
        if trimmed = path then path
        else trimSeparators trimmed

    /// Hashes an already canonical directory without interpreting its path syntax.
    let hashCanonicalDirectory (directory: string | null) (port: int) : Result<string, Failure> =
        if not (validPort port) then
            Result.Error(TargetFailure.InvalidPort)
        else
            match directory with
            | null -> Result.Error(TargetFailure.InvalidDirectory)
            | directory when String.IsNullOrWhiteSpace directory ->
                Result.Error(TargetFailure.InvalidDirectory)
            | directory ->
                let decimalPort = port.ToString(CultureInfo.InvariantCulture)
                $"hermes-owner-control-v1\n{directory}\n{decimalPort}\n"
                |> Encoding.UTF8.GetBytes
                |> SHA256.HashData
                |> Convert.ToHexString
                |> fun hash -> Result.Ok(hash.ToLowerInvariant())

    let private canonicalize (directory: string | null) : Result<string, Failure> =
        match directory with
        | null -> Result.Error(TargetFailure.InvalidDirectory)
        | directory when String.IsNullOrWhiteSpace directory || not (Path.IsPathFullyQualified directory) ->
            Result.Error(TargetFailure.InvalidDirectory)
        | directory ->
            try
                let canonical = directory |> Path.GetFullPath |> trimSeparators
                Result.Ok(if OperatingSystem.IsWindows() then canonical.ToUpperInvariant() else canonical)
            with
            | :? ArgumentException
            | :? NotSupportedException
            | :? PathTooLongException -> Result.Error(TargetFailure.InvalidDirectory)

    /// Lexical only: performs no configuration, filesystem, or symlink reads.
    let tryCreate (directory: string | null) (port: int) : Result<Target, Failure> =
        if not (validPort port) then
            Result.Error(TargetFailure.InvalidPort)
        else
            canonicalize directory
            |> Result.bind (fun canonical ->
                hashCanonicalDirectory canonical port
                |> Result.map (fun key ->
                    { CanonicalDirectory = canonical
                      Port = port
                      ConfigKey = key
                      PipeName = "hmop1-" + key.Substring(0, 32)
                      ApiOrigin = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) }))
