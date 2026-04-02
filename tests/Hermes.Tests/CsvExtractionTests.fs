module Hermes.Tests.CsvExtractionTests

open Xunit
open Hermes.Core

// ─── parseCsvLine ────────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``CsvExtraction_ParseCsvLine_Comma_SplitsCorrectly`` () =
    let fields = CsvExtraction.parseCsvLine ',' "Alice,100,true"
    Assert.Equal(3, fields.Length)
    Assert.Equal("Alice", fields.[0])
    Assert.Equal("100", fields.[1])

[<Fact>]
[<Trait("Category", "Unit")>]
let ``CsvExtraction_ParseCsvLine_QuotedFieldWithComma`` () =
    let fields = CsvExtraction.parseCsvLine ',' "\"Smith, John\",42"
    Assert.Equal(2, fields.Length)
    Assert.Equal("Smith, John", fields.[0])

[<Fact>]
[<Trait("Category", "Unit")>]
let ``CsvExtraction_ParseCsvLine_EmptyFields`` () =
    let fields = CsvExtraction.parseCsvLine ',' "a,,c"
    Assert.Equal(3, fields.Length)
    Assert.Equal("", fields.[1])

[<Fact>]
[<Trait("Category", "Unit")>]
let ``CsvExtraction_ParseCsvLine_Semicolon`` () =
    let fields = CsvExtraction.parseCsvLine ';' "a;b;c"
    Assert.Equal(3, fields.Length)

// ─── detectDelimiter ─────────────────────────────────────────────────

[<Theory>]
[<InlineData("a,b,c\n1,2,3", ',')>]
[<InlineData("a;b;c\n1;2;3", ';')>]
[<InlineData("a\tb\tc\n1\t2\t3", '\t')>]
[<Trait("Category", "Unit")>]
let ``CsvExtraction_DetectDelimiter_DetectsCorrectly`` (csv: string, expected: char) =
    Assert.Equal(expected, CsvExtraction.detectDelimiter csv)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``CsvExtraction_DetectDelimiter_DefaultsToComma`` () =
    Assert.Equal(',', CsvExtraction.detectDelimiter "nodels")

// ─── extractCsv ──────────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``CsvExtraction_ExtractCsv_ProducesContent`` () =
    let csv = "Name,Amount\nAlice,100\nBob,200"
    let result = CsvExtraction.extractCsv csv
    Assert.True(result.Pages.Length > 0)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``CsvExtraction_ExtractCsv_EmptyString_EmptyPages`` () =
    let result = CsvExtraction.extractCsv ""
    Assert.True(result.Pages.Length = 0 || result.Pages.[0].Blocks.Length = 0)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``CsvExtraction_ExtractCsv_HeaderOnly`` () =
    let result = CsvExtraction.extractCsv "Name,Amount"
    Assert.NotNull(result)
