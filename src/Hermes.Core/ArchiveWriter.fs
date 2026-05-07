#nowarn "3261"

namespace Hermes.Core

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading.Tasks

/// File-first archive writer for the structured account/sender-domain/subject-slug hierarchy.
/// Pure functions compute paths and file names; I/O functions use the FileSystem algebra.
[<RequireQualifiedAccess>]
module ArchiveWriter =

    // ─── Types ──────────────────────────────────────────────────────

    type SidecarFile =
        { Name: string
          MimeType: string
          SizeBytes: int64
          Sha256: string }

    type SidecarData =
        { Version: int
          SourceType: string
          Account: string
          ProviderId: string
          ThreadId: string
          Sender: string option
          Subject: string option
          ReceivedAt: string
          Files: SidecarFile list }

    // ─── JSON serialisation ─────────────────────────────────────────

    let private jsonOptions =
        let opts = JsonSerializerOptions(WriteIndented = true)
        opts.PropertyNamingPolicy <- JsonNamingPolicy.SnakeCaseLower
        opts

    // ─── Compiled regexes ───────────────────────────────────────────

    let private nonAlphanumRegex =
        Regex(@"[^a-z0-9]+", RegexOptions.Compiled)

    let private multiHyphenRegex =
        Regex(@"-{2,}", RegexOptions.Compiled)

    let private angleBracketEmailRegex =
        Regex(@"<([^>]+)>", RegexOptions.Compiled)

    // ─── Pure functions ─────────────────────────────────────────────

    /// Convert text to a filesystem-safe slug (lowercase, hyphens, max 60 chars).
    let slugify (text: string) : string =
        if String.IsNullOrWhiteSpace(text) then
            "untitled"
        else
            text.ToLowerInvariant()
            |> fun s -> nonAlphanumRegex.Replace(s, "-")
            |> fun s -> multiHyphenRegex.Replace(s, "-")
            |> fun s -> s.Trim('-')
            |> fun s ->
                if s.Length <= 60 then s
                else
                    let truncated = s.Substring(0, 60)
                    match truncated.LastIndexOf('-') with
                    | idx when idx > 30 -> truncated.Substring(0, idx)
                    | _ -> truncated
            |> fun s -> if String.IsNullOrEmpty(s) then "untitled" else s

    /// Extract domain from an email sender string.
    let extractSenderDomain (sender: string) : string =
        if String.IsNullOrWhiteSpace(sender) then
            "unknown"
        else
            let email =
                match angleBracketEmailRegex.Match(sender) with
                | m when m.Success -> m.Groups.[1].Value
                | _ -> sender

            match email.Trim().LastIndexOf('@') with
            | idx when idx >= 0 && idx < email.Length - 1 ->
                email.Substring(idx + 1).Trim().ToLowerInvariant()
            | _ -> "unknown"

    /// Compute relative folder path: account/senderDomain/subjectSlug--threadIdPrefix.
    /// Thread ID suffix ensures uniqueness when subject + sender collide (e.g. recurring receipts).
    let threadFolderPath (account: string) (senderDomain: string) (subjectSlug: string) (threadId: string) : string =
        let threadSuffix =
            if String.IsNullOrWhiteSpace(threadId) then ""
            else
                let clean = slugify threadId
                let short = if clean.Length > 8 then clean.[..7] else clean
                $"--{short}"
        Path.Combine(slugify account, slugify senderDomain, $"{slugify subjectSlug}{threadSuffix}")

    /// Compute relative folder path for watch-folder drops: local/yyyy-MM-dd.fileNameSlug.
    let localFolderPath (date: DateTimeOffset) (fileNameSlug: string) : string =
        let dateStr = date.ToString("yyyy-MM-dd")
        let folder = $"{dateStr}.{slugify fileNameSlug}"
        Path.Combine("local", folder)

    /// Format date for file prefix: yyyy-MM-dd.
    let datePrefix (date: DateTimeOffset) : string =
        date.ToString("yyyy-MM-dd")

    /// Build file name for an email body markdown file, with message ID suffix for uniqueness.
    let messageFileName (date: DateTimeOffset) (description: string) (messageId: string) : string =
        let idSuffix =
            if String.IsNullOrWhiteSpace(messageId) then ""
            else
                let short = slugify messageId
                let trunc = if short.Length > 6 then short.[..5] else short
                $"-{trunc}"
        $"{datePrefix date}-{slugify description}{idSuffix}.md"

    /// Build file name for an attachment, preserving the original extension. Content hash suffix prevents collisions.
    let attachmentFileName (date: DateTimeOffset) (originalName: string) (contentHash: string) : string =
        let ext = Path.GetExtension(originalName)
        let stem = Path.GetFileNameWithoutExtension(originalName)
        let hashSuffix =
            if String.IsNullOrWhiteSpace(contentHash) then ""
            else $"-{contentHash.[..min 5 (contentHash.Length - 1)]}"
        $"{datePrefix date}-{slugify stem}{hashSuffix}{ext}"

    // ─── I/O functions ──────────────────────────────────────────────

    /// Create the directory (idempotent), return the absolute path.
    let ensureFolder (fs: Algebra.FileSystem) (archiveRoot: string) (relativePath: string) : string =
        let fullPath = Path.Combine(archiveRoot, relativePath)
        fs.createDirectory fullPath
        fullPath

    /// Write a message markdown file to the folder, return the file name.
    let writeMessage
        (fs: Algebra.FileSystem)
        (folderPath: string)
        (date: DateTimeOffset)
        (description: string)
        (messageId: string)
        (bodyMarkdown: string)
        : Task<string> =
        task {
            let fileName = messageFileName date description messageId
            let fullPath = Path.Combine(folderPath, fileName)
            do! fs.writeAllText fullPath bodyMarkdown
            return fileName
        }

    /// Write an attachment to the folder, return the file name.
    let writeAttachment
        (fs: Algebra.FileSystem)
        (folderPath: string)
        (date: DateTimeOffset)
        (originalName: string)
        (contentHash: string)
        (content: byte array)
        : Task<string> =
        task {
            let fileName = attachmentFileName date originalName contentHash
            let fullPath = Path.Combine(folderPath, fileName)
            do! fs.writeAllBytes fullPath content
            return fileName
        }

    /// Write extracted markdown alongside an attachment file.
    let writeExtraction (fs: Algebra.FileSystem) (attachmentPath: string) (markdown: string) : Task<unit> =
        let extractionPath = $"{attachmentPath}.extracted.md"
        fs.writeAllText extractionPath markdown

    /// Write thread comprehension JSON to the folder.
    let writeComprehension (fs: Algebra.FileSystem) (folderPath: string) (json: string) : Task<unit> =
        let fullPath = Path.Combine(folderPath, "thread.comprehension.json")
        fs.writeAllText fullPath json

    /// Write or merge sidecar metadata (.hermes.json) to the folder.
    /// If a sidecar already exists, merges the file list (appends new files).
    let writeSidecar (fs: Algebra.FileSystem) (folderPath: string) (metadata: SidecarData) : Task<unit> =
        task {
            let fullPath = Path.Combine(folderPath, ".hermes.json")
            let! merged =
                task {
                    if fs.fileExists fullPath then
                        try
                            let! existing = fs.readAllText fullPath
                            let existingData = JsonSerializer.Deserialize<SidecarData>(existing, jsonOptions)
                            let existingNames = existingData.Files |> List.map (fun f -> f.Name) |> Set.ofList
                            let newFiles = metadata.Files |> List.filter (fun f -> not (existingNames.Contains f.Name))
                            return { existingData with Files = existingData.Files @ newFiles }
                        with _ -> return metadata
                    else return metadata
                }
            let json = JsonSerializer.Serialize(merged, jsonOptions)
            do! fs.writeAllText fullPath json
        }

    // ─── Read functions ─────────────────────────────────────────────

    /// Read all markdown files in a folder, sorted by filename (chronological).
    let readThreadMessages (fs: Algebra.FileSystem) (folderPath: string) : Task<string list> =
        task {
            let files =
                fs.getFiles folderPath "*.md"
                |> Array.filter (fun f -> not (f.EndsWith(".extracted.md", StringComparison.OrdinalIgnoreCase)))
                |> Array.sort

            let! contents =
                files
                |> Array.map fs.readAllText
                |> Task.WhenAll

            return contents |> Array.toList
        }

    /// Read extracted markdown for an attachment, if it exists.
    let readExtraction (fs: Algebra.FileSystem) (attachmentPath: string) : Task<string option> =
        task {
            let extractionPath = $"{attachmentPath}.extracted.md"

            if fs.fileExists extractionPath then
                let! content = fs.readAllText extractionPath
                return Some content
            else
                return None
        }

    /// Read thread comprehension JSON, if it exists.
    let readComprehension (fs: Algebra.FileSystem) (folderPath: string) : Task<string option> =
        task {
            let fullPath = Path.Combine(folderPath, "thread.comprehension.json")

            if fs.fileExists fullPath then
                let! content = fs.readAllText fullPath
                return Some content
            else
                return None
        }
