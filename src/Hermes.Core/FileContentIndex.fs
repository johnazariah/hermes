namespace Hermes.Core

open System.Threading.Tasks

/// One file-content projection per stable document identity.
[<RequireQualifiedAccess>]
module internal FileContentIndex =

    type Kind =
        | EmailBody
        | Extraction

    module Kind =
        let toString = function
            | EmailBody -> "source"
            | Extraction -> "extraction"

    type Content =
        { Kind: Kind
          Text: string }

    type Writer = string -> (string * obj) list -> Task<int>

    let clear (write: Writer) (documentId: int64) =
        task {
            let! _ =
                write "DELETE FROM document_content_fts WHERE rowid = @id"
                    [ "@id", Database.boxVal documentId ]
            return ()
        }

    /// The caller supplies a transaction-scoped writer.
    let replace (write: Writer) (documentId: int64) (content: Content) =
        task {
            let! _ =
                write
                    """DELETE FROM document_content_fts WHERE rowid = @id;
                       INSERT INTO document_content_fts(rowid, text, kind)
                       VALUES (@id, @text, @kind);"""
                    [ "@id", Database.boxVal documentId
                      "@text", Database.boxVal content.Text
                      "@kind", Database.boxVal (Kind.toString content.Kind) ]
            return ()
        }
