module Hermes.Tests.LocalReindexCommandTests

open System
open System.Collections.Concurrent
open Xunit
open Hermes.Service

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Explicit IDs default to dry run and retain requested order`` () =
    let request =
        ReindexCommand.parse [ "--document-id"; "3"; "--document-id"; "1" ]
        |> Result.defaultWith failwith
    Assert.Equal(ReindexCommand.Mode.DryRun, request.Mode)
    Assert.Equal<int64 list>([ 3L; 1L ], request.DocumentIds)
    Assert.True(request.ConfigPath.IsNone)

[<Theory>]
[<InlineData("--apply", true)>]
[<InlineData("--dry-run", false)>]
[<Trait("Category", "Unit")>]
let ``An explicit mode selects exactly that mode`` flag apply =
    let request =
        ReindexCommand.parse [ flag; "--document-id"; "1"; "--config"; "local.yaml" ]
        |> Result.defaultWith failwith
    Assert.Equal((if apply then ReindexCommand.Mode.Apply else ReindexCommand.Mode.DryRun), request.Mode)
    Assert.Equal(Some "local.yaml", request.ConfigPath)

[<Theory>]
[<InlineData("0")>]
[<InlineData("-1")>]
[<InlineData("+1")>]
[<InlineData("1,2")>]
[<InlineData("1-4")>]
[<InlineData("all")>]
[<InlineData(" 1")>]
[<InlineData("9223372036854775808")>]
[<Trait("Category", "Unit")>]
let ``Nonpositive implicit ranged or malformed IDs fail closed`` value =
    let result = ReindexCommand.parse [ "--document-id"; value ]
    Assert.True(Result.isError result)

[<Theory>]
[<InlineData("")>]
[<InlineData("--apply")>]
[<InlineData("--document-id")>]
[<InlineData("--all")>]
[<InlineData("1")>]
[<Trait("Category", "Unit")>]
let ``Missing IDs and unknown or incomplete arguments are rejected`` (text: string) =
    let arguments = text.Split(' ', StringSplitOptions.RemoveEmptyEntries) |> Array.toList
    Assert.True(ReindexCommand.parse arguments |> Result.isError)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Duplicate IDs modes and configuration flags are rejected`` () =
    [ [ "--document-id"; "1"; "--document-id"; "1" ]
      [ "--document-id"; "1"; "--apply"; "--dry-run" ]
      [ "--document-id"; "1"; "--apply"; "--apply" ]
      [ "--document-id"; "1"; "--config"; "one"; "--config"; "two" ]
      [ "--document-id"; "1"; "--config"; "--apply" ] ]
    |> List.iter (fun arguments ->
        Assert.True(ReindexCommand.parse arguments |> Result.isError))

[<Theory>]
[<InlineData(100, true)>]
[<InlineData(101, false)>]
[<Trait("Category", "Unit")>]
let ``Command selection has an explicit document bound`` count accepted =
    let arguments =
        List.init count (fun index -> [ "--document-id"; string (index + 1) ]) |> List.concat
    Assert.Equal(accepted, ReindexCommand.parse arguments |> Result.isOk)

[<Theory>]
[<InlineData("REINDEX-FILES")>]
[<InlineData("reindex-file")>]
[<InlineData("--apply")>]
[<InlineData("--document-id")>]
[<Trait("Category", "Unit")>]
let ``Malformed operator invocations never fall through to host startup`` argument =
    let output = ConcurrentQueue<string>()
    let context =
        { ServiceEntry.localContext Hermes.Core.Interpreters.realFileSystem Hermes.Core.Interpreters.systemEnvironment with
            OpenArchive = fun _ _ -> failwith "Unexpected archive open" }
    let code =
        ServiceEntry.runWith context output.Enqueue
            (fun _ -> failwith "Unexpected service startup") [| argument |]
    Assert.Equal(2, code)
    let json = output |> Seq.exactlyOne |> HttpRegressionFixture.json
    Assert.Equal(0, json.GetProperty("outcomes").GetArrayLength())
    Assert.Equal(2, json.GetProperty("exitCode").GetInt32())

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Ordinary service arguments retain the existing entry path`` () =
    let output = ConcurrentQueue<string>()
    let context =
        { ServiceEntry.localContext Hermes.Core.Interpreters.realFileSystem Hermes.Core.Interpreters.systemEnvironment with
            OpenArchive = fun _ _ -> failwith "Unexpected operator archive open" }
    let start (arguments: string array) =
        Assert.Equal<string>([| "--initial-sync-days"; "7" |], arguments)
        73
    let code = ServiceEntry.runWith context output.Enqueue start [| "--initial-sync-days"; "7" |]
    Assert.Equal(73, code)
    Assert.Empty(output)
