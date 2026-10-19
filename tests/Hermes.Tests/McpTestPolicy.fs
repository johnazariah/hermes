module Hermes.Tests.McpTestPolicy

open Hermes.Core

// Synthetic test credential only; never loaded by production composition.
let token = "synthetic-test-only-mcp-token-not-a-real-credential"

let policy =
    McpAccess.configure true (Some token) |> Result.defaultWith failwith

let capabilities: McpServer.Capabilities =
    { Access = McpAccess.authenticate policy (Some("Bearer " + token))
      QueryEncoder = None }

/// Existing dispatcher mutation contracts run explicitly authenticated.
/// New default-policy tests use Hermes.Core.McpServer directly.
module Server =
    type JsonRpcResponse = Hermes.Core.McpServer.JsonRpcResponse
    let parseRequest = Hermes.Core.McpServer.parseRequest
    let serialiseResponse = Hermes.Core.McpServer.serialiseResponse
    let toolDefinitions = Hermes.Core.McpServer.toolDefinitions
    let processMessage =
        Hermes.Core.McpServer.processMessageWithCapabilities capabilities
