module Hermes.Tests.ArchiveWriterTests

#nowarn "3261"

open System
open Xunit
open Hermes.Core

// ─── slugify ─────────────────────────────────────────────────────────

[<Theory>]
[<InlineData("Your March 2026 Bill", "your-march-2026-bill")>]
[<InlineData("Re: Re: FW: stuff", "re-re-fw-stuff")>]
[<InlineData("HELLO WORLD", "hello-world")>]
[<InlineData("invoice #1234", "invoice-1234")>]
[<Trait("Category", "Unit")>]
let ``ArchiveWriter_Slugify_NormalText_ReturnsSlug`` (input: string, expected: string) =
    Assert.Equal(expected, ArchiveWriter.slugify input)

[<Theory>]
[<InlineData("")>]
[<InlineData("   ")>]
[<Trait("Category", "Unit")>]
let ``ArchiveWriter_Slugify_EmptyOrWhitespace_ReturnsUntitled`` (input: string) =
    Assert.Equal("untitled", ArchiveWriter.slugify input)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ArchiveWriter_Slugify_LongText_TruncatesAt60Chars`` () =
    let long = String.replicate 20 "word "
    let result = ArchiveWriter.slugify long
    Assert.True(result.Length <= 60, $"Slug too long: {result.Length}")
    Assert.False(result.EndsWith("-"), "Should not end with hyphen")

// ─── extractSenderDomain ─────────────────────────────────────────────

[<Theory>]
[<InlineData("user@example.com", "example.com")>]
[<InlineData("Name <user@telstra.com.au>", "telstra.com.au")>]
[<InlineData("HR Dept <payroll@microsoft.com>", "microsoft.com")>]
[<Trait("Category", "Unit")>]
let ``ArchiveWriter_ExtractSenderDomain_ValidEmail_ReturnsDomain`` (sender: string, expected: string) =
    Assert.Equal(expected, ArchiveWriter.extractSenderDomain sender)

[<Theory>]
[<InlineData("")>]
[<InlineData("no-at-sign")>]
[<InlineData("   ")>]
[<Trait("Category", "Unit")>]
let ``ArchiveWriter_ExtractSenderDomain_NoEmail_ReturnsUnknown`` (sender: string) =
    Assert.Equal("unknown", ArchiveWriter.extractSenderDomain sender)

// ─── Path computation ────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ArchiveWriter_ThreadFolderPath_BuildsCorrectPath`` () =
    let path = ArchiveWriter.threadFolderPath "john@gmail.com" "telstra.com.au" "your-march-bill" "thread-abc123"
    Assert.Contains("john-gmail-com", path)
    Assert.Contains("telstra-com-au", path)
    Assert.Contains("your-march-bill", path)
    Assert.Contains("thread-a", path)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ArchiveWriter_ThreadFolderPath_DifferentThreadIds_DifferentPaths`` () =
    let path1 = ArchiveWriter.threadFolderPath "a" "b.com" "same-subject" "thread-111"
    let path2 = ArchiveWriter.threadFolderPath "a" "b.com" "same-subject" "thread-222"
    Assert.NotEqual<string>(path1, path2)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ArchiveWriter_LocalFolderPath_BuildsCorrectPath`` () =
    let date = DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero)
    let path = ArchiveWriter.localFolderPath date "bank-statement"
    Assert.Contains("local", path)
    Assert.Contains("2026-03-15", path)
    Assert.Contains("bank-statement", path)

// ─── File naming ─────────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ArchiveWriter_MessageFileName_IncludesDateAndSlug`` () =
    let date = DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero)
    let name = ArchiveWriter.messageFileName date "initial report" "msg-001"
    Assert.StartsWith("2026-03-15-", name)
    Assert.EndsWith(".md", name)
    Assert.Contains("initial-report", name)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ArchiveWriter_AttachmentFileName_PreservesExtension`` () =
    let date = DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero)
    let name = ArchiveWriter.attachmentFileName date "Telstra Bill March.pdf" "abc123"
    Assert.StartsWith("2026-03-15-", name)
    Assert.EndsWith(".pdf", name)
    Assert.Contains("telstra-bill-march", name)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ArchiveWriter_AttachmentFileName_HandlesDotInName`` () =
    let date = DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero)
    let name = ArchiveWriter.attachmentFileName date "invoice.v2.pdf" "def456"
    Assert.EndsWith(".pdf", name)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ArchiveWriter_AttachmentFileName_DifferentHashes_DifferentNames`` () =
    let date = DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero)
    let name1 = ArchiveWriter.attachmentFileName date "invoice.pdf" "aaaaaa"
    let name2 = ArchiveWriter.attachmentFileName date "invoice.pdf" "bbbbbb"
    Assert.NotEqual<string>(name1, name2)

// ─── I/O functions ───────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ArchiveWriter_WriteMessage_CreatesFile`` () =
    let m = TestHelpers.memFs()
    let date = DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero)
    let folder = "/archive/test-folder"
    m.Fs.createDirectory folder
    let fileName = ArchiveWriter.writeMessage m.Fs folder date "test message" "msg-001" "Hello world" |> Async.AwaitTask |> Async.RunSynchronously
    Assert.EndsWith(".md", fileName)
    let content = m.Get(folder + "/" + fileName)
    Assert.Equal(Some "Hello world", content)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ArchiveWriter_WriteAttachment_CreatesFile`` () =
    let m = TestHelpers.memFs()
    let date = DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero)
    let folder = "/archive/test-folder"
    m.Fs.createDirectory folder
    let content = [| 1uy; 2uy; 3uy |]
    let fileName = ArchiveWriter.writeAttachment m.Fs folder date "report.pdf" "sha256hash" content |> Async.AwaitTask |> Async.RunSynchronously
    Assert.EndsWith(".pdf", fileName)
    Assert.True(m.Fs.fileExists(folder + "/" + fileName))

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ArchiveWriter_WriteExtraction_CreatesSidecarFile`` () =
    let m = TestHelpers.memFs()
    let attPath = "/archive/test/2026-03-15-report.pdf"
    ArchiveWriter.writeExtraction m.Fs attPath "# Extracted text" |> Async.AwaitTask |> Async.RunSynchronously
    let content = m.Get(attPath + ".extracted.md")
    Assert.Equal(Some "# Extracted text", content)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ArchiveWriter_WriteComprehension_CreatesThreadJson`` () =
    let m = TestHelpers.memFs()
    let folder = "/archive/test-folder"
    m.Fs.createDirectory folder
    let json = """{"thread_summary":"test"}"""
    ArchiveWriter.writeComprehension m.Fs folder json |> Async.AwaitTask |> Async.RunSynchronously
    let content = m.Get(folder + "/thread.comprehension.json")
    Assert.Equal(Some json, content)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ArchiveWriter_WriteSidecar_CreatesHermesJson`` () =
    let m = TestHelpers.memFs()
    let folder = "/archive/test-folder"
    m.Fs.createDirectory folder
    let sidecar : ArchiveWriter.SidecarData =
        { Version = 2; SourceType = "email_attachment"; Account = "test"
          ProviderId = "msg1"; ThreadId = "t1"; Sender = Some "bob@example.com"
          Subject = Some "Test"; ReceivedAt = "2026-03-15T10:00:00Z"; Files = [] }
    ArchiveWriter.writeSidecar m.Fs folder sidecar |> Async.AwaitTask |> Async.RunSynchronously
    let content = m.Get(folder + "/.hermes.json")
    Assert.True(content.IsSome, "Sidecar file not created")
    Assert.Contains("email_attachment", content.Value)
    Assert.Contains("provider_id", content.Value)

// ─── Read functions ──────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ArchiveWriter_ReadExtraction_ReturnsNone_WhenNotExists`` () =
    let m = TestHelpers.memFs()
    let result = ArchiveWriter.readExtraction m.Fs "/nonexistent.pdf" |> Async.AwaitTask |> Async.RunSynchronously
    Assert.Equal(None, result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ArchiveWriter_ReadComprehension_ReturnsNone_WhenNotExists`` () =
    let m = TestHelpers.memFs()
    let result = ArchiveWriter.readComprehension m.Fs "/nonexistent-folder" |> Async.AwaitTask |> Async.RunSynchronously
    Assert.Equal(None, result)

// ─── Publication fence folder identity ───────────────────────────────

let private fenceIdentities archiveRoot savedPath folderPath =
    PublicationFence.ArtifactFolder.tryFromMetadata archiveRoot savedPath folderPath
    |> Option.map PublicationFence.ArtifactFolder.identities
    |> Option.defaultWith (fun () -> failwith "Expected an artifact folder")

let private canonicalOf (path: string) =
    let full =
        System.IO.Path.GetFullPath(path)
        |> System.IO.Path.TrimEndingDirectorySeparator
    if System.OperatingSystem.IsWindows() then full.ToUpperInvariant() else full

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PublicationFence_RelativeAndAbsoluteSiblings_ShareTheSameGate`` () =
    // One physical folder, two persisted metadata forms: an archive-relative
    // saved_path and an absolute, trailing-separator folder_path.
    let relative = fenceIdentities "/archive" "invoice.pdf" None
    let absolute = fenceIdentities "/archive" "/archive/statement.pdf" (Some "/archive/")
    Assert.NotEmpty(relative)
    Assert.Equal<string list>(relative, absolute)
    // Stable multi-key ordering: sorted and duplicate free, so acquiring any
    // key set in stripe order cannot deadlock against another key set.
    Assert.Equal<string list>(relative, List.sort relative)
    Assert.Equal<string list>(relative, List.distinct relative)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PublicationFence_RelativeIdentity_ResolvesAgainstArchiveRootNotCwd`` () =
    let identities = fenceIdentities "/archive" "payslips/test.pdf" None
    let rooted = canonicalOf (System.IO.Path.Combine("/archive", "payslips"))
    let workingDirectory = canonicalOf "payslips"
    Assert.True(List.contains rooted identities, "Identity must resolve against the archive root")
    Assert.False(List.contains workingDirectory identities, "Identity must not resolve against the process CWD")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PublicationFence_UnknownArchiveRoot_MeetsRootedCallerOnSharedGate`` () =
    // Reflow fences without an archive root. Its identities must always be a
    // subset of the identities a rooted publisher claims for the same folder.
    let assertSubset savedPath =
        let rooted = fenceIdentities "/archive" savedPath None
        let unrooted = fenceIdentities PublicationFence.UnknownArchiveRoot savedPath None
        Assert.NotEmpty(unrooted)
        for key in unrooted do
            Assert.True(List.contains key rooted, $"Unshared gate for '{savedPath}': {key}")
    assertSubset "payslips/test.pdf"
    assertSubset "/archive/payslips/test.pdf"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ArchiveWriter_ReadExtraction_ReturnsContent_WhenExists`` () =
    let m = TestHelpers.memFs()
    m.Put "/test/report.pdf.extracted.md" "# Extracted"
    let result = ArchiveWriter.readExtraction m.Fs "/test/report.pdf" |> Async.AwaitTask |> Async.RunSynchronously
    Assert.Equal(Some "# Extracted", result)
