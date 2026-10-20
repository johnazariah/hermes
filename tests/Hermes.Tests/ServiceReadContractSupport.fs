module Hermes.Tests.ServiceReadContractSupport

open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open Hermes.Core

type Server = HttpRegressionFixture.Server

type Reply =
    { Body: string
      MediaType: string }

type FileSnapshot =
    { Text: Map<string, string>
      Bytes: Map<string, string>
      Directories: Map<string, bool> }

type PreservedState =
    { Metadata: Map<string, Map<string, obj> list>
      Files: FileSnapshot }

type Snapshot =
    { DatabaseHash: string
      Schema: Map<string, obj> list
      Preserved: PreservedState }

// Logical route contracts use a real synthetic owner-approved API session.
// HTTP authority and boundary assertions live in the dedicated route-access tests.
let withServer verify = HttpRegressionFixture.withServer None verify

let execute (server: Server) (sql: string) (parameters: (string * obj) list) : Task<unit> =
    task {
        let! _ = server.Db.execNonQuery sql parameters
        return ()
    }

let rows (server: Server) (sql: string) (parameters: (string * obj) list) =
    server.Db.execReader sql parameters

let seed (server: Server) (name: string) =
    HttpRegressionFixture.seedSource server
        $"/archive/service-contracts/{name}/source.png" $"synthetic immutable source: {name}"

let text (name: string) (element: JsonElement) =
    element.GetProperty(name).GetString()
    |> Option.ofObj
    |> Option.defaultWith (fun () -> invalidOp $"Missing string property: {name}")

let optionValue (element: JsonElement) =
    if element.ValueKind = JsonValueKind.Object then element.GetProperty("value")
    else element

let optionalText (name: string) (element: JsonElement) =
    let value = element.GetProperty(name)
    if value.ValueKind = JsonValueKind.Null then None
    else (optionValue value).GetString() |> Option.ofObj

let ids (element: JsonElement) =
    element.EnumerateArray()
    |> Seq.map (fun item -> item.GetProperty("id").GetInt64())
    |> Seq.toList

let private mediaType (content: HttpContent) =
    content.Headers.ContentType
    |> Option.ofObj
    |> Option.bind (fun header -> header.MediaType |> Option.ofObj)
    |> Option.defaultValue ""

let private receive (server: Server) (request: HttpRequestMessage) expected : Task<Reply> =
    task {
        use cancellation = new CancellationTokenSource(HttpRegressionFixture.timeout)
        use! response = server.Client.SendAsync(request, cancellation.Token)
        Assert.Equal(expected, int response.StatusCode)
        let! body = response.Content.ReadAsStringAsync(cancellation.Token)
        return { Body = body; MediaType = mediaType response.Content }
    }

let read server (path: string) expected : Task<Reply> =
    task {
        use request = new HttpRequestMessage(HttpMethod.Get, path)
        return! receive server request expected
    }

let writeText server (method: HttpMethod) (path: string) (mime: string) (body: string) expected =
    task {
        use request = new HttpRequestMessage(method, path)
        request.Content <- new StringContent(body, Encoding.UTF8, mime)
        return! receive server request expected
    }

let decode (reply: Reply) =
    Assert.Equal("application/json", reply.MediaType)
    HttpRegressionFixture.json reply.Body

let getJson server path : Task<JsonElement> =
    task {
        let! reply = read server path 200
        return decode reply
    }

let postJson server path (body: obj | null) : Task<JsonElement> =
    task {
        let! reply =
            writeText server HttpMethod.Post path "application/json"
                (JsonSerializer.Serialize(body)) 200
        return decode reply
    }

let files (server: Server) : FileSnapshot =
    { Text = server.Files.Files |> Seq.map (fun item -> item.Key, item.Value) |> Map.ofSeq
      Bytes =
        server.Files.Bytes
        |> Seq.map (fun item -> item.Key, SourceFiles.hash item.Value)
        |> Map.ofSeq
      Directories = server.Files.Dirs |> Seq.map (fun item -> item.Key, item.Value) |> Map.ofSeq }

let private removeFields fields document =
    fields |> List.fold (fun state field -> state |> Map.remove field) document

let preserve ignoredTables ignoredDocumentFields server : Task<PreservedState> =
    task {
        let! metadata = FileKeywordFixtures.metadataSnapshot server
        let retained = ignoredTables |> List.fold (fun state table -> Map.remove table state) metadata
        let filtered =
            retained
            |> Map.change "documents" (Option.map (List.map (removeFields ignoredDocumentFields)))
        return { Metadata = filtered; Files = files server }
    }

let snapshot server : Task<Snapshot> =
    task {
        let! databaseHash = RetrievalHttpFixtures.databaseHash server
        let! schema = rows server "SELECT type,name,tbl_name,sql FROM sqlite_schema ORDER BY type,name" []
        let! preserved = preserve [] [] server
        return { DatabaseHash = databaseHash; Schema = schema; Preserved = preserved }
    }

let assertUnchanged server before : Task<unit> =
    task {
        let! after = snapshot server
        Assert.Equal<Snapshot>(before, after)
    }

let assertPreserved tables fields server before : Task<unit> =
    task {
        let! after = preserve tables fields server
        Assert.Equal<PreservedState>(before, after)
    }
