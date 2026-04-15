namespace Hermes.Core

#nowarn "3261"

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading.Tasks

/// Harvest contacts from comprehension output and store them in
/// the contacts + document_contacts tables.
[<RequireQualifiedAccess>]
module ContactExtraction =

    // ─── Types ───────────────────────────────────────────────────────

    type ContactData =
        { Name: string
          CanonicalName: string
          Email: string option
          Abn: string option
          Phone: string option
          Address: string option
          ContactType: string
          TaxRelevant: bool option
          SourceSender: string option }

    // ─── Name normalisation ──────────────────────────────────────────

    let private suffixPattern =
        Regex(
            @"\b(Pty\.?\s*Ltd\.?|Inc\.?|Ltd\.?|Limited|Corporation|Corp\.?)\s*$",
            RegexOptions.IgnoreCase ||| RegexOptions.Compiled)

    let normaliseName (name: string) : string =
        name
        |> fun s -> suffixPattern.Replace(s, "")
        |> fun s -> s.Replace("\"", "").Replace("'", "")
        |> fun s -> s.Trim().ToLowerInvariant()

    // ─── Deterministic ID ────────────────────────────────────────────

    let computeContactId (canonicalName: string) (abn: string option) : string =
        let key =
            match abn with
            | Some a -> $"{canonicalName}|{a}"
            | None -> canonicalName

        SHA256.HashData(Encoding.UTF8.GetBytes(key))
        |> Convert.ToHexString
        |> fun hex -> hex.Substring(0, 16).ToLowerInvariant()

    // ─── Contact type mapping ────────────────────────────────────────

    let contactTypeFromSender (senderType: SenderClassification.SenderType) : string =
        match senderType with
        | SenderClassification.SenderType.Bank -> "supplier"
        | SenderClassification.SenderType.PropertyManager -> "supplier"
        | SenderClassification.SenderType.Employer -> "employer"
        | SenderClassification.SenderType.Government -> "government"
        | SenderClassification.SenderType.Utility -> "supplier"
        | SenderClassification.SenderType.Insurance -> "supplier"
        | SenderClassification.SenderType.Retailer -> "supplier"
        | SenderClassification.SenderType.Unknown -> "unknown"

    let private contactTypeFromDocType (docType: string) : string =
        match docType.Trim().ToLowerInvariant() with
        | "invoice" | "utility-bill"
        | "agent-statement" | "rental-statement" -> "supplier"
        | "payslip" | "payroll-statement" -> "employer"
        | "tax-return" | "land-tax"
        | "council-rates" | "payg-instalment" -> "government"
        | "insurance-policy" | "insurance-renewal" -> "supplier"
        | "bank-statement" | "credit-card-statement" -> "supplier"
        | _ -> "unknown"

    // ─── JSON helpers ────────────────────────────────────────────────

    let tryGetString (elem: JsonElement) (prop: string) : string option =
        match elem.TryGetProperty(prop) with
        | true, v when v.ValueKind = JsonValueKind.String ->
            let s = v.GetString()
            if String.IsNullOrWhiteSpace(s) then None else Some s
        | _ -> None

    let private tryGetNested (elem: JsonElement) (parent: string) (child: string) : string option =
        match elem.TryGetProperty(parent) with
        | true, nested when nested.ValueKind = JsonValueKind.Object ->
            tryGetString nested child
        | _ -> None

    let private firstOf (candidates: string option list) : string option =
        candidates |> List.tryPick id

    // ─── Email extraction ────────────────────────────────────────────

    let private emailRegex =
        Regex(@"[\w.+-]+@[\w.-]+\.\w{2,}", RegexOptions.Compiled)

    let private extractEmail (sender: string) : string option =
        let m = emailRegex.Match(sender)
        if m.Success then Some m.Value else None

    // ─── Harvest from comprehension JSON ─────────────────────────────

    let private resolveContactType (root: JsonElement) : string =
        tryGetString root "document_type"
        |> Option.map contactTypeFromDocType
        |> Option.defaultValue "unknown"

    let private resolveName (root: JsonElement) : string option =
        [ tryGetString root "sender_name"
          tryGetNested root "fields" "sender_name"
          tryGetNested root "fields" "employer" ]
        |> firstOf

    let private resolveField (root: JsonElement) (topKey: string) (fieldKey: string) : string option =
        [ tryGetString root topKey
          tryGetNested root "fields" fieldKey ]
        |> firstOf

    let harvestFromComprehension (json: string) (sender: string option) : ContactData option =
        try
            use doc = JsonDocument.Parse(json)
            let root = doc.RootElement

            match resolveName root with
            | None -> None
            | Some name ->
                let canonical = normaliseName name
                let abn = resolveField root "abn" "abn"
                let phone = resolveField root "phone" "phone"
                let address = resolveField root "address" "address"
                let contactType = resolveContactType root
                let email = sender |> Option.bind extractEmail

                Some
                    { Name = name
                      CanonicalName = canonical
                      Email = email
                      Abn = abn
                      Phone = phone
                      Address = address
                      ContactType = contactType
                      TaxRelevant = None
                      SourceSender = sender }
        with _ ->
            None

    // ─── Database helpers ────────────────────────────────────────────

    let private optParam (value: string option) : obj =
        match value with
        | Some v -> Database.boxVal v
        | None -> box DBNull.Value

    let private boolParam (value: bool option) : obj =
        match value with
        | Some true -> Database.boxVal 1L
        | Some false -> Database.boxVal 0L
        | None -> box DBNull.Value

    // ─── Resolve existing contact ────────────────────────────────────

    let private tryFindByAbn (db: Algebra.Database) (abn: string) : Task<string option> =
        task {
            let! result =
                db.execScalar
                    "SELECT id FROM contacts WHERE abn = @abn LIMIT 1"
                    [ ("@abn", Database.boxVal abn) ]

            return
                match result with
                | null | :? DBNull -> None
                | v -> Some (v :?> string)
        }

    let private tryFindByName (db: Algebra.Database) (canonical: string) : Task<string option> =
        task {
            let! result =
                db.execScalar
                    "SELECT id FROM contacts WHERE canonical_name = @name LIMIT 1"
                    [ ("@name", Database.boxVal canonical) ]

            return
                match result with
                | null | :? DBNull -> None
                | v -> Some (v :?> string)
        }

    let resolveExistingContact (db: Algebra.Database) (contact: ContactData) : Task<string option> =
        task {
            match contact.Abn with
            | Some abn ->
                let! byAbn = tryFindByAbn db abn
                match byAbn with
                | Some _ -> return byAbn
                | None -> return! tryFindByName db contact.CanonicalName
            | None ->
                return! tryFindByName db contact.CanonicalName
        }

    // ─── Upsert contact ─────────────────────────────────────────────

    let private updateExisting (db: Algebra.Database) (id: string) (contact: ContactData) : Task<unit> =
        task {
            let! _ =
                db.execNonQuery
                    """UPDATE contacts SET
                        last_seen_at = datetime('now'),
                        email = COALESCE(@email, email),
                        abn = COALESCE(@abn, abn),
                        phone = COALESCE(@phone, phone),
                        address = COALESCE(@address, address)
                    WHERE id = @id"""
                    [ ("@id", Database.boxVal id)
                      ("@email", optParam contact.Email)
                      ("@abn", optParam contact.Abn)
                      ("@phone", optParam contact.Phone)
                      ("@address", optParam contact.Address) ]
            ()
        }

    let private insertNew (db: Algebra.Database) (id: string) (contact: ContactData) : Task<unit> =
        task {
            let! _ =
                db.execNonQuery
                    """INSERT INTO contacts
                        (id, name, canonical_name, email, abn, phone, address,
                         contact_type, tax_relevant, source_sender,
                         first_seen_at, last_seen_at)
                    VALUES
                        (@id, @name, @canonical, @email, @abn, @phone, @address,
                         @type, @tax, @sender,
                         datetime('now'), datetime('now'))"""
                    [ ("@id", Database.boxVal id)
                      ("@name", Database.boxVal contact.Name)
                      ("@canonical", Database.boxVal contact.CanonicalName)
                      ("@email", optParam contact.Email)
                      ("@abn", optParam contact.Abn)
                      ("@phone", optParam contact.Phone)
                      ("@address", optParam contact.Address)
                      ("@type", Database.boxVal contact.ContactType)
                      ("@tax", boolParam contact.TaxRelevant)
                      ("@sender", optParam contact.SourceSender) ]
            ()
        }

    let upsertContact (db: Algebra.Database) (contact: ContactData) : Task<string> =
        task {
            let! existing = resolveExistingContact db contact

            match existing with
            | Some id ->
                do! updateExisting db id contact
                return id
            | None ->
                let id = computeContactId contact.CanonicalName contact.Abn
                do! insertNew db id contact
                return id
        }

    // ─── Link document to contact ────────────────────────────────────

    let linkDocument (db: Algebra.Database) (documentId: int64) (contactId: string) (role: string) : Task<unit> =
        task {
            let! _ =
                db.execNonQuery
                    "INSERT OR IGNORE INTO document_contacts (document_id, contact_id, role) VALUES (@doc, @contact, @role)"
                    [ ("@doc", Database.boxVal documentId)
                      ("@contact", Database.boxVal contactId)
                      ("@role", Database.boxVal role) ]
            ()
        }

    // ─── Main entry point ────────────────────────────────────────────

    let harvestAndLink
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (documentId: int64)
        (comprehensionJson: string)
        (sender: string option)
        : Task<unit> =
        task {
            try
                match harvestFromComprehension comprehensionJson sender with
                | None ->
                    logger.debug $"ContactExtraction: no contact data for doc {documentId}"
                | Some contact ->
                    let! contactId = upsertContact db contact
                    do! linkDocument db documentId contactId "issuer"
                    logger.info $"ContactExtraction: linked doc {documentId} → contact {contactId} ({contact.Name})"
            with ex ->
                logger.error $"ContactExtraction: failed for doc {documentId}: {ex.Message}"
        }
