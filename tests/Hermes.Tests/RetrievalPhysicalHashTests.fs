module Hermes.Tests.RetrievalPhysicalHashTests

open System
open System.IO
open Xunit
open Hermes.Core
open Hermes.Tests.RetrievalHttpFixtures

let private withDirectory verify =
    task {
        let directory = Path.Combine(Path.GetTempPath(), "hermes-physical-hash-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(directory) |> ignore
        try
            do! verify (Path.Combine(directory, "synthetic.sqlite"))
        finally Directory.Delete(directory, true)
    }

let private openWriter (path: string) =
    new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite,
                   bufferSize = 4096, useAsync = true)

let private replaceContents (stream: FileStream) (bytes: byte array) =
    task {
        stream.Position <- 0L
        stream.SetLength(0L)
        do! stream.WriteAsync(ReadOnlyMemory<byte>(bytes))
        do! stream.FlushAsync()
    }

let private assertHashes path (expected: Map<string, byte array>) =
    task {
        let! actual = physicalHashes path
        let digests = expected |> Map.map (fun _ bytes -> SourceFiles.hash bytes)
        Assert.Equal<Map<string, string>>(digests, actual)
    }

let private verifyLiveFiles (path: string) =
    task {
        let walPath = path + "-wal"
        use database = openWriter path
        use wal = openWriter walPath
        let original = Map.ofList [ path, [| 0uy; 1uy; 128uy; 255uy |]; walPath, [||] ]
        do! replaceContents database original.[path]
        do! assertHashes path original
        let databaseChanged = original |> Map.add path [| 0uy; 2uy; 128uy; 255uy |]
        do! replaceContents database databaseChanged.[path]
        do! assertHashes path databaseChanged
        let walChanged = databaseChanged |> Map.add walPath [| 255uy; 0uy; 7uy; 8uy; 9uy |]
        do! replaceContents wal walChanged.[walPath]
        do! assertHashes path walChanged
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Physical hashes include database and empty WAL and detect changed bytes with readwrite handles open`` () =
    withDirectory verifyLiveFiles

let private verifyDatabaseOnly (path: string) =
    task {
        use database = openWriter path
        let bytes = [| 0uy; 255uy; 1uy |]
        do! replaceContents database bytes
        do! assertHashes path (Map.ofList [ path, bytes ])
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Physical hashes preserve the database filename and omit an absent WAL with its handle open`` () =
    withDirectory verifyDatabaseOnly
