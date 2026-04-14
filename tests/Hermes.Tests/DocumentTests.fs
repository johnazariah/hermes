module Hermes.Tests.DocumentTests

#nowarn "3261"

open System
open Xunit
open Hermes.Core

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Document_decode_string_returns_Some_when_present`` () =
    let doc = Map.ofList [ "name", box "test.pdf" ] : Document.T
    let result = doc |> Document.decode<string> "name"
    Assert.Equal(Some "test.pdf", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Document_decode_returns_None_for_missing_key`` () =
    let doc = Map.empty : Document.T
    let result = doc |> Document.decode<string> "missing"
    Assert.Equal(None, result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Document_decode_returns_None_for_DBNull`` () =
    let doc = Map.ofList [ "val", box DBNull.Value ] : Document.T
    let result = doc |> Document.decode<string> "val"
    Assert.Equal(None, result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Document_decode_returns_None_for_null`` () =
    let doc = Map.ofList [ "val", (null : obj) ] : Document.T
    let result = doc |> Document.decode<string> "val"
    Assert.Equal(None, result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Document_decode_int64_from_int64`` () =
    let doc = Map.ofList [ "id", box 42L ] : Document.T
    let result = doc |> Document.decode<int64> "id"
    Assert.Equal(Some 42L, result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Document_encode_adds_key`` () =
    let doc = Map.empty |> Document.encode "stage" (box "received")
    Assert.Equal(Some "received", doc |> Document.decode<string> "stage")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Document_encode_overwrites_key`` () =
    let doc =
        Map.empty
        |> Document.encode "stage" (box "received")
        |> Document.encode "stage" (box "extracted")
    Assert.Equal(Some "extracted", doc |> Document.decode<string> "stage")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Document_hasKey_true_for_present_value`` () =
    let doc = Map.ofList [ "text", box "hello" ] : Document.T
    Assert.True(Document.hasKey "text" doc)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Document_hasKey_false_for_DBNull`` () =
    let doc = Map.ofList [ "text", box DBNull.Value ] : Document.T
    Assert.False(Document.hasKey "text" doc)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Document_hasKey_false_for_missing`` () =
    let doc = Map.empty : Document.T
    Assert.False(Document.hasKey "missing" doc)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Document_id_returns_id_field`` () =
    let doc = Map.ofList [ "id", box 99L ] : Document.T
    Assert.Equal(99L, Document.id doc)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Document_stage_returns_default_when_missing`` () =
    let doc = Map.empty : Document.T
    Assert.Equal("received", Document.stage doc)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Document_fromRow_is_identity`` () =
    let row = Map.ofList [ "id", box 1L; "stage", box "extracted" ]
    let doc = Document.fromRow row
    Assert.Equal(Some 1L, doc |> Document.decode<int64> "id")
    Assert.Equal(Some "extracted", doc |> Document.decode<string> "stage")
