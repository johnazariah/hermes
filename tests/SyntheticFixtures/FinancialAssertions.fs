module Hermes.Tests.SyntheticFixtures.FinancialAssertions

open System
open Xunit
open Hermes.Core

type Extraction =
    { Content: PdfStructure.DocumentContent
      Markdown: string
      SourceTrace: string }

let private render content =
    { Content = content
      Markdown = PdfStructure.toMarkdown content Map.empty
      SourceTrace = "" }

let private pdfTrace bytes =
    let letters =
        PdfStructure.extractLetters bytes
        |> List.map (fun (page, values) ->
            let text = values |> List.map (fun letter -> letter.Value) |> String.concat ""
            $"page {page}: {text}")
        |> String.concat "\n"
    let lines =
        PdfStructure.extractLines bytes
        |> List.collect (fun (page, values) ->
            values |> List.map (fun line ->
                let words =
                    line.Words |> List.map (fun word -> $"[{word.Text}]") |> String.concat " "
                $"page {page}, y={line.Y:R}: {line.Text}\nwords: {words}"))
        |> String.concat "\n"
    $"--- RAW PDF LETTER VALUES ---\n{letters}\n--- EXTRACTED LINES ---\n{lines}"

let private readPdf bytes =
    let content = PdfStructure.extractStructured bytes
    if List.isEmpty content.Pages then
        Error "SYNTHETIC PDF fixture has no extractable pages"
    else
        let extraction = render content
        Ok { extraction with SourceTrace = pdfTrace bytes }

let contains (terms: string list) (extraction: Extraction) =
    terms
    |> List.iter (fun term ->
        Assert.True(
            extraction.Markdown.Contains(term, StringComparison.Ordinal),
            $"Missing required term: {term}\n{extraction.SourceTrace}\n--- FULL SYNTHETIC MARKDOWN ---\n{extraction.Markdown}\n--- END SYNTHETIC MARKDOWN ---"))

let pdf bytes =
    let extraction = bytes |> readPdf |> Result.defaultWith invalidOp
    Assert.Single(extraction.Content.Pages) |> ignore
    contains [ "SYNTHETIC" ] extraction
    extraction

let csv text =
    let extraction = text |> CsvExtraction.extractCsv |> render
    Assert.Single(extraction.Content.Pages) |> ignore
    Assert.Equal(0.85, extraction.Content.Confidence)
    contains [ "SYNTHETIC" ] extraction
    extraction

let confidence minimum extraction =
    Assert.InRange(extraction.Content.Confidence, minimum, 1.0)

let table headers rows extraction =
    let actual =
        extraction.Content.Pages
        |> List.collect (fun page -> page.Blocks)
        |> List.choose (function PdfStructure.TableBlock value -> Some value | _ -> None)
        |> Assert.Single
    Assert.Equal<string list>(headers, actual.Headers)
    Assert.Equal<string list list>(rows, actual.Rows)
    let markdownRow cells = "| " + String.concat " | " cells + " |"
    let separator = headers |> List.map (fun _ -> "---")
    headers :: separator :: rows
    |> List.map markdownRow
    |> fun lines -> Assert.Contains(String.concat "\n" lines, extraction.Markdown)
