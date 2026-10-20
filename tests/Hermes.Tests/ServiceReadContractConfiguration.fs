module Hermes.Tests.ServiceReadContractConfiguration

open Xunit
open Hermes.Core
open Hermes.Tests.ServiceReadContractSupport

let private preparePreferences mode (server: Server) =
    match mode with
    | 0 -> ""
    | 1 ->
        server.Files.Put "/config/config.yaml" "preferences: synthetic fallback"
        "synthetic fallback"
    | 2 ->
        server.Files.Put "/config/config.yaml" "preferences: ["
        ""
    | _ ->
        server.Files.Put "/config/config.yaml" "preferences: synthetic fallback"
        let supplied = "synthetic override\nsecond line — Ω\n"
        server.Files.Put "/config/preferences.txt" supplied
        supplied

[<Theory>]
[<InlineData(0)>]
[<InlineData(1)>]
[<InlineData(2)>]
[<InlineData(3)>]
[<Trait("Category", "Integration")>]
let ``HTTP preferences use synthetic override config fallback or empty fallback`` mode =
    withServer (fun server ->
        task {
            let expected = preparePreferences mode server
            let! before = snapshot server
            let! reply = read server "/api/preferences" 200
            Assert.Equal("text/plain", reply.MediaType)
            Assert.Equal(expected, reply.Body)
            do! assertUnchanged server before
        })

let private accountConfig =
    """accounts:
  - label: synthetic-a
    provider: gmail
  - label: synthetic-b
    provider: outlook
"""

let private syncSql =
    """INSERT INTO sync_state(account,last_sync_at,message_count)
       VALUES ('synthetic-a','2025-03-01T00:00:00Z',42),
              ('unconfigured-synthetic','2025-03-02T00:00:00Z',99);"""

[<Fact>]
[<Trait("Category", "Integration")>]
let ``HTTP sync accounts merge configured labels without exposing unconfigured state`` () =
    withServer (fun server ->
        task {
            server.Files.Put "/config/config.yaml" accountConfig
            do! execute server syncSql []
            let! before = snapshot server
            let! accounts = getJson server "/api/sync/accounts"
            Assert.Equal(2, accounts.GetArrayLength())
            Assert.Equal("synthetic-a", text "account" accounts.[0])
            Assert.Equal(Some "2025-03-01T00:00:00Z", optionalText "lastSyncAt" accounts.[0])
            Assert.Equal(42L, accounts.[0].GetProperty("messageCount").GetInt64())
            Assert.Equal("synthetic-b", text "account" accounts.[1])
            Assert.Equal(None, optionalText "lastSyncAt" accounts.[1])
            Assert.Equal(0L, accounts.[1].GetProperty("messageCount").GetInt64())
            do! assertUnchanged server before
        })

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
[<Trait("Category", "Integration")>]
let ``HTTP absent or invalid synthetic account configuration does not expose database labels`` invalidConfig =
    withServer (fun server ->
        task {
            if invalidConfig then server.Files.Put "/config/config.yaml" "accounts: ["
            do! execute server syncSql []
            let! before = snapshot server
            let! accounts = getJson server "/api/sync/accounts"
            Assert.Equal(0, accounts.GetArrayLength())
            do! assertUnchanged server before
        })
