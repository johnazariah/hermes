module Hermes.Tests.OwnerControlTargetTests

open System
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Xunit
open Hermes.Core

let private ok result =
    result |> Result.defaultWith (fun failure -> failwithf "Unexpected target rejection: %A" failure)

let private fixtureText (name: string) (value: JsonElement) : string =
    match value.GetProperty(name).GetString() with
    | null -> failwith "Expected a non-null synthetic fixture string."
    | text -> text

[<Fact>]
let ``naming vectors use the same exact preimages as the native client`` () =
    let fixture = Path.Combine(AppContext.BaseDirectory, "SyntheticFixtures", "http-access-v1.json")
    use document = JsonDocument.Parse(File.ReadAllText fixture)
    for vector in document.RootElement.GetProperty("naming").EnumerateArray() do
        let directory = fixtureText "canonicalDirectory" vector
        let port = vector.GetProperty("port").GetInt32()
        let preimage = fixtureText "preimage" vector
        Assert.Equal($"hermes-owner-control-v1\n{directory}\n{port.ToString(CultureInfo.InvariantCulture)}\n", preimage)
        let expected = preimage |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString
        Assert.Equal(expected.ToLowerInvariant(), OwnerControlTarget.hashCanonicalDirectory directory port |> ok)

[<Fact>]
let ``normalization preserves roots and removes every nonroot trailing separator`` () =
    let root = if OperatingSystem.IsWindows() then @"C:\" else "/"
    let directory = Path.Combine(root, "synthetic", "Hermes", "config")
    let trailing = directory + String(Path.DirectorySeparatorChar, 3)
    let expected = if OperatingSystem.IsWindows() then directory.ToUpperInvariant() else directory
    let target = OwnerControlTarget.tryCreate trailing 31991 |> ok
    Assert.Equal(expected, target.CanonicalDirectory)
    Assert.Equal(root, (OwnerControlTarget.tryCreate root 31991 |> ok).CanonicalDirectory)
    Assert.Equal("hmop1-" + target.ConfigKey.Substring(0, 32), target.PipeName)
    Assert.Equal(64, target.ConfigKey.Length)
    Assert.Equal("http://127.0.0.1:31991", target.ApiOrigin)

[<Fact>]
let ``lexical dot segments normalize without filesystem or configuration reads`` () =
    let root = if OperatingSystem.IsWindows() then @"C:\" else "/"
    let direct = Path.Combine(root, "synthetic", "hermes-owner-wire")
    let alias = Path.Combine(root, "synthetic", "unused", "..", "hermes-owner-wire", ".")
    Assert.Equal(OwnerControlTarget.tryCreate direct 31991, OwnerControlTarget.tryCreate alias 31991)

[<Theory>]
[<InlineData("")>]
[<InlineData(" ")>]
[<InlineData("relative")>]
[<InlineData("./relative")>]
let ``relative and empty directories are rejected`` (directory: string) =
    Assert.Equal<Result<OwnerControlTarget.Target, OwnerControlTarget.Failure>>(
        Result.Error(OwnerControlTarget.Failure.InvalidDirectory),
        OwnerControlTarget.tryCreate directory 31991)

[<Fact>]
let ``null directories return typed failures from both naming entry points`` () =
    let absent: string | null = null
    Assert.Equal<Result<OwnerControlTarget.Target, OwnerControlTarget.Failure>>(
        Result.Error(OwnerControlTarget.Failure.InvalidDirectory),
        OwnerControlTarget.tryCreate absent 31991)
    Assert.Equal<Result<string, OwnerControlTarget.Failure>>(
        Result.Error(OwnerControlTarget.Failure.InvalidDirectory),
        OwnerControlTarget.hashCanonicalDirectory absent 31991)

[<Theory>]
[<InlineData(0)>]
[<InlineData(-1)>]
[<InlineData(65536)>]
let ``invalid ports are rejected`` (port: int) =
    Assert.Equal<Result<OwnerControlTarget.Target, OwnerControlTarget.Failure>>(
        Result.Error(OwnerControlTarget.Failure.InvalidPort),
        OwnerControlTarget.tryCreate "/" port)

[<Fact>]
let ``canonical hashing preserves Unix case and separates ports and directories`` () =
    let hash directory port = OwnerControlTarget.hashCanonicalDirectory directory port |> ok
    Assert.NotEqual<string>(hash "/synthetic/Hermes" 31991, hash "/synthetic/hermes" 31991)
    Assert.NotEqual<string>(hash "/synthetic/Hermes" 31991, hash "/synthetic/Hermes" 31992)
    Assert.NotEqual<string>(hash "/synthetic/Hermes" 31991, hash "/synthetic/Other" 31991)
