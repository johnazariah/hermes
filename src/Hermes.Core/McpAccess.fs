namespace Hermes.Core

open System
open System.Security.Cryptography
open System.Text

[<RequireQualifiedAccess>]
module McpAccess =

    type Capability = Read | Mutate
    type Policy = private Disabled | Enabled of byte array
    type Session = private Session of bool

    let readOnly = Disabled
    let anonymous = Session false

    let private validToken (token: string) =
        token.Length >= 32 && token.Length <= 4096
        && not (token |> Seq.exists Char.IsWhiteSpace)

    let private digest (token: string) =
        token |> Encoding.UTF8.GetBytes |> SHA256.HashData

    let configure enabled token =
        if not enabled then Ok Disabled
        else
            token |> Option.filter validToken
            |> Option.map (digest >> Enabled >> Ok)
            |> Option.defaultValue (Error "Enabled MCP mutations require a non-whitespace token of 32–4096 characters")

    let private bearer (authorization: string) =
        if authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) then
            let token = authorization.Substring(7)
            if validToken token then Some token else None
        else None

    let private equal (expected: byte array) supplied =
        let actual = digest supplied
        CryptographicOperations.FixedTimeEquals(
            ReadOnlySpan<byte>(expected), ReadOnlySpan<byte>(actual))

    let authenticate policy authorization =
        match policy with
        | Disabled -> anonymous
        | Enabled expected ->
            authorization |> Option.bind bearer
            |> Option.exists (equal expected)
            |> Session

    let permits (Session authenticated) = function
        | Read -> true
        | Mutate -> authenticated
