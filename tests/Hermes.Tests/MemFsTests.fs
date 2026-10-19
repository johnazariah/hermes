module Hermes.Tests.MemFsTests

open System
open System.IO
open System.Text
open Xunit
open Hermes.Core

let private leaf = "memfs-alias.txt"
let private archiveFile = "/archive/" + leaf

let private assertFile (memory: TestHelpers.MemFs) (expected: byte array) () path =
    task {
        let expectedText = Encoding.UTF8.GetString expected
        Assert.True(memory.Fs.fileExists path)
        Assert.Equal<int64>(expected.LongLength, memory.Fs.getFileSize path)
        let! bytes = memory.Fs.readAllBytes path
        let! text = memory.Fs.readAllText path
        Assert.Equal<byte array>(expected, bytes)
        Assert.Equal<string>(SourceFiles.hash expected, SourceFiles.hash bytes)
        Assert.Equal<string>(expectedText, text)
        Assert.Equal<string option>(Some expectedText, memory.Get path)
    }

let private assertAliases memory expected =
    [ archiveFile; Path.GetFullPath archiveFile ]
    |> Prelude.foldTask (assertFile memory expected) ()

let private writeText (memory: TestHelpers.MemFs) path (text: string) =
    task {
        do! memory.Fs.writeAllText path text
        do! assertAliases memory (Encoding.UTF8.GetBytes text)
    }

let private writeBytes (memory: TestHelpers.MemFs) path bytes =
    task {
        do! memory.Fs.writeAllBytes path bytes
        do! assertAliases memory bytes
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``MemFs rooted and current-drive canonical aliases share text bytes size and hashes`` () =
    task {
        let memory = TestHelpers.memFs ()
        let canonical = Path.GetFullPath archiveFile
        let initial = "synthetic café evidence"
        Assert.Equal<string>(archiveFile, memory.Norm canonical)
        memory.Put archiveFile initial
        do! assertAliases memory (Encoding.UTF8.GetBytes initial)
        do! writeText memory canonical "replacement résumé evidence"
        do! writeBytes memory archiveFile [| 0uy; 65uy; 255uy |]
        do! writeBytes memory canonical [| 0uy; 66uy; 254uy; 67uy |]
        Assert.Equal<string>(archiveFile, Assert.Single<string>(memory.Files.Keys))
        Assert.Equal<string>(archiveFile, Assert.Single<string>(memory.Bytes.Keys))
    }

let private distinctPaths () =
    let canonical = Path.GetFullPath archiveFile
    let otherDrives =
        [ 'A' .. 'Z' ]
        |> List.filter (fun drive ->
            not (canonical.StartsWith(string drive + ":", StringComparison.OrdinalIgnoreCase)))
        |> List.take 2
        |> List.map (fun drive -> $"{drive}:/archive/{leaf}")
    let otherKeys =
        [ "archive/"; "../archive/"; "/outside/"; "/archive-neighbour/"
          "//server/share/archive/"; "C:archive/"; "D:archive/" ]
        |> List.map (fun prefix -> prefix + leaf)
    let doubledRoot = Path.GetFullPath("/").Replace('\\', '/') + "/archive/" + leaf
    otherKeys @ otherDrives @ [ doubledRoot ]

let private assertDistinct (memory: TestHelpers.MemFs) () path =
    task {
        Assert.Equal<string>(path, memory.Norm path)
        Assert.NotEqual<string>(archiveFile, memory.Norm path)
        Assert.False(memory.Fs.fileExists path)
        let bytes = Encoding.UTF8.GetBytes(path: string)
        do! memory.Fs.writeAllBytes path bytes
        do! assertFile memory bytes () path
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``MemFs retains other volumes UNC relative paths and unrelated roots as distinct keys`` () =
    task {
        let memory = TestHelpers.memFs ()
        let original = "original archive evidence"
        let paths = distinctPaths ()
        memory.Put archiveFile original
        do! paths |> Prelude.foldTask (assertDistinct memory) ()
        do! assertAliases memory (Encoding.UTF8.GetBytes original)
        Assert.Equal(paths.Length + 1, memory.Files.Count)
        Assert.Equal(paths.Length, memory.Bytes.Count)
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``MemFs directory listing movement and deletion use the same rooted aliases`` () =
    let memory = TestHelpers.memFs ()
    let folder = "/archive/memfs-alias"
    let canonical = Path.GetFullPath folder
    let child = folder + "/child"
    let destination = folder + "/" + leaf
    memory.Fs.createDirectory canonical
    Assert.True(memory.Fs.directoryExists folder)
    memory.Fs.createDirectory child
    Assert.Equal<string array>([| child |], memory.Fs.getDirectories canonical)
    memory.Put archiveFile "movable evidence"
    memory.Fs.moveFile (Path.GetFullPath archiveFile) (Path.GetFullPath destination)
    Assert.False(memory.Fs.fileExists archiveFile)
    Assert.True(memory.Fs.fileExists destination)
    Assert.Equal<string array>([| destination |], memory.Fs.getFiles canonical "*")
    memory.Fs.deleteFile (Path.GetFullPath destination)
    Assert.False(memory.Fs.fileExists destination)
