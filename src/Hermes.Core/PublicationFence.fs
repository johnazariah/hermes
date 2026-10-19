namespace Hermes.Core

open System
open System.IO
open System.Threading
open System.Threading.Tasks

/// Process-wide mutual exclusion for document generations and shared artifacts.
[<RequireQualifiedAccess>]
module PublicationFence =

    [<Literal>]
    let UnknownArchiveRoot = ""

    /// Canonical identities derived from a document's persisted folder metadata.
    type ArtifactFolder =
        private
            { MetadataPath: string
              Identities: string list }

    [<RequireQualifiedAccess>]
    module ArtifactFolder =

        let private nonBlank (value: string) =
            if String.IsNullOrWhiteSpace value then None
            else Some(value.Trim())

        let private folderFromSavedPath savedPath =
            match nonBlank savedPath with
            | None -> None
            | Some path ->
                try
                    match Path.GetDirectoryName(path) with
                    | null -> Some "."
                    | parent when String.IsNullOrWhiteSpace parent -> Some "."
                    | parent -> Some(parent.Trim())
                with _ ->
                    None

        let private normaliseCase (value: string) =
            if OperatingSystem.IsWindows() then value.ToUpperInvariant()
            else value

        let private canonicalIdentity path =
            try
                let fullPath = Path.GetFullPath(path)
                let trimmed = Path.TrimEndingDirectorySeparator(fullPath)
                let canonical =
                    if String.IsNullOrWhiteSpace trimmed then fullPath
                    else trimmed
                Some(normaliseCase canonical)
            with _ ->
                None

        let private syntheticRoot =
            if OperatingSystem.IsWindows() then
                @"C:\__hermes_archive_identity__"
            else
                "/__hermes_archive_identity__"

        let private archiveRelativeIdentity path =
            try
                let fullPath = Path.GetFullPath(path, syntheticRoot)
                let relative = Path.GetRelativePath(syntheticRoot, fullPath)
                let trimmed = Path.TrimEndingDirectorySeparator(relative)
                let canonical =
                    if String.IsNullOrWhiteSpace trimmed then "."
                    else trimmed
                Some $"archive-relative:{normaliseCase canonical}"
            with _ ->
                None

        let private escapesArchiveRoot (relativePath: string) =
            let startsWithParent separator =
                relativePath.StartsWith(
                    ".." + string separator,
                    StringComparison.Ordinal)
            relativePath = ".."
            || startsWithParent Path.DirectorySeparatorChar
            || startsWithParent Path.AltDirectorySeparatorChar
            || Path.IsPathRooted relativePath

        let private relativeIdentityFromRoot root path =
            try
                let relative = Path.GetRelativePath(root, path)
                if escapesArchiveRoot relative then None
                else archiveRelativeIdentity relative
            with _ ->
                None

        let private combinedIdentity root path =
            try
                Path.Combine(root, path) |> canonicalIdentity
            with _ ->
                None

        let private identitiesFor (archiveRoot: string) (path: string) =
            try
                let root =
                    archiveRoot
                    |> nonBlank
                    |> Option.bind canonicalIdentity
                let absolute =
                    if Path.IsPathRooted path then canonicalIdentity path
                    else root |> Option.bind (fun value -> combinedIdentity value path)
                let relative =
                    if Path.IsPathRooted path then
                        match root, absolute with
                        | Some rootPath, Some absolutePath ->
                            relativeIdentityFromRoot rootPath absolutePath
                        | _ -> None
                    else
                        archiveRelativeIdentity path
                [ absolute; relative ] |> List.choose id
            with _ ->
                []

        let private candidate archiveRoot path =
            match identitiesFor archiveRoot path with
            | [] -> None
            | identities -> Some(path, identities)

        let private tryFromMetadataCore archiveRoot savedPath folderPath =
            let candidates =
                [ folderPath |> Option.bind nonBlank
                  folderFromSavedPath savedPath ]
                |> List.choose id
                |> List.choose (candidate archiveRoot)

            match candidates with
            | [] -> None
            | (metadataPath, _) :: _ ->
                let identities =
                    candidates
                    |> List.collect snd
                    |> List.distinct
                    |> List.sort
                Some
                    { MetadataPath = metadataPath
                      Identities = identities }

        type MetadataResolver private () =
            static member Resolve
                (savedPath: string, folderPath: string option)
                : ArtifactFolder option =
                tryFromMetadataCore UnknownArchiveRoot savedPath folderPath

            static member Resolve
                (archiveRoot: string, savedPath: string)
                : (string option -> ArtifactFolder option) =
                tryFromMetadataCore archiveRoot savedPath

        /// Resolves folder_path first, while retaining saved_path's parent as an
        /// alias so equivalent persisted metadata participates in the same fence.
        let inline tryFromMetadata
            (first: string)
            (second: ^argument)
            : ^result =
            ((^argument or MetadataResolver) :
                (static member Resolve :
                    string * ^argument -> ^result)
                    (first, second))

        let identities (folder: ArtifactFolder) =
            folder.Identities

        /// Resolves the persisted folder to the path used for filesystem I/O.
        let resolve
            (archiveRoot: string)
            (folder: ArtifactFolder)
            : Result<string, string> =
            try
                if Path.IsPathRooted folder.MetadataPath then
                    Ok(Path.TrimEndingDirectorySeparator(folder.MetadataPath))
                elif String.IsNullOrWhiteSpace archiveRoot then
                    Error "Archive root is required for a relative artifact folder"
                elif folder.MetadataPath = "." then
                    Ok(Path.TrimEndingDirectorySeparator(archiveRoot))
                else
                    Path.Combine(archiveRoot, folder.MetadataPath)
                    |> Path.TrimEndingDirectorySeparator
                    |> Ok
            with error ->
                Error $"Invalid artifact folder '{folder.MetadataPath}': {error.Message}"

    let [<Literal>] private StripeCount = 64

    let private gates =
        Array.init StripeCount (fun _ -> new SemaphoreSlim(1, 1))

    let private heldGateIndices =
        AsyncLocal<Set<int> option>()

    let private stableHash value =
        value
        |> Seq.fold
            (fun hash character ->
                (hash ^^^ uint32 (int character)) * 16777619u)
            2166136261u

    let private gateIndex key =
        int (stableHash key % uint32 StripeCount)

    let private gateIndicesFor keys =
        keys
        |> List.map gateIndex
        |> List.distinct
        |> List.sort

    let rec private acquire (acquired: int list) (remaining: int list) =
        task {
            match remaining with
            | [] -> return acquired
            | index :: rest ->
                do! gates.[index].WaitAsync()
                return! acquire (index :: acquired) rest
        }

    let private release (acquired: int list) =
        acquired
        |> List.iter (fun index -> gates.[index].Release() |> ignore)

    let private currentlyHeld () =
        heldGateIndices.Value
        |> Option.defaultValue Set.empty

    let private withKeys keys (body: unit -> Task<'a>) : Task<'a> =
        task {
            let requested = gateIndicesFor keys
            let previouslyHeld = currentlyHeld ()
            let pending =
                requested
                |> List.filter (fun index ->
                    not (Set.contains index previouslyHeld))
            let! acquired = acquire [] pending
            heldGateIndices.Value <-
                requested
                |> Set.ofList
                |> Set.union previouslyHeld
                |> Some
            try
                return! body ()
            finally
                heldGateIndices.Value <-
                    if Set.isEmpty previouslyHeld then None
                    else Some previouslyHeld
                release acquired
        }

    let private documentKey documentId =
        $"document:{documentId}"

    let private artifactKeys (folder: ArtifactFolder) =
        folder.Identities
        |> List.map (fun identity -> $"artifact-folder:{identity}")

    /// Runs a body with exclusive access to one document.
    let withDocument documentId body =
        withKeys [ documentKey documentId ] body

    /// Acquires document and folder resources in stable stripe order.
    let withDocumentAndArtifact documentId folder body =
        documentKey documentId :: artifactKeys folder
        |> fun keys -> withKeys keys body
