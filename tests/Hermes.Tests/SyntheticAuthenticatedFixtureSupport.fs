namespace Hermes.Tests

open System
open System.Collections.Concurrent
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Hermes.Core.LocalAccess
open Hermes.Service

/// Only synthetic fixtures retain OwnerAccess. HTTP clients receive a scoped session.
module SyntheticAuthenticatedFixtureSupport =
    type Authority =
        { InstanceId: InstanceId
          ConfigKey: string
          Runtime: TaskCompletionSource<LocalAccessRuntime> }

    type Running =
        { Identity: Identity
          Runtime: LocalAccessRuntime
          Audience: Uri }

    let timeout: TimeSpan = TimeSpan.FromSeconds 10.0
    let cancellation: CancellationToken = CancellationToken.None
    let all: Set<Capability> =
        Set.ofList [ Capability.ConfigRead; Capability.ModelInvoke; Capability.Mutate ]
    let epoch: DateTimeOffset = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)

    let private checkedValue (value: Result<'Value, AccessError>) : 'Value =
        value |> Result.defaultWith (fun _ -> invalidOp "Synthetic authority operation failed.")

    let create () : Authority =
        { InstanceId = RandomNumberGenerator.GetBytes(16) |> InstanceId.ofBytes |> checkedValue
          ConfigKey =
            Guid.NewGuid().ToString("N") |> Encoding.UTF8.GetBytes |> SHA256.HashData
            |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
          Runtime = TaskCompletionSource<LocalAccessRuntime>(
              TaskCreationOptions.RunContinuationsAsynchronously) }

    let attach
        (app: WebApplication) (authority: Authority) (availability: ModelAvailability) : unit =
        let publicAccess () : PublicAccess option =
            if authority.Runtime.Task.IsCompletedSuccessfully then
                Some authority.Runtime.Task.Result.Public
            else None
        HttpAccessPolicy.attach app authority.InstanceId publicAccess availability
        LocalAccessHttp.mapEndpoints app authority.InstanceId

    let private address (app: WebApplication) : Uri =
        let server = app.Services.GetRequiredService<IServer>()
        let addresses =
            server.Features.Get<IServerAddressesFeature>() |> Option.ofObj
            |> Option.defaultWith (fun () -> invalidOp "Missing synthetic listener address.")
        addresses.Addresses |> Seq.exactlyOne |> Uri

    let activate
        (app: WebApplication) (authority: Authority) (origins: string list) : Running =
        let audience = address app
        let identity: Identity =
            Identity.create authority.InstanceId 1 epoch
                (ApiAudience.create audience.Port |> checkedValue) authority.ConfigKey |> checkedValue
        let clock: Clock = { Read = fun () -> { Elapsed = TimeSpan.Zero; Utc = epoch } }
        let entropy: Entropy = { Bytes = fun size -> RandomNumberGenerator.GetBytes(size) }
        let extras = origins |> List.map (BrowserOrigin.tryParse >> checkedValue) |> Set.ofList
        let runtime =
            LocalAccessRuntime.createWith clock entropy { Unavailable = fun () -> () }
                identity all extras |> checkedValue
        authority.Runtime.SetResult(runtime)
        { Identity = identity; Runtime = runtime; Audience = audience }

    let startReady (running: Running) : Task<unit> =
        task {
            let! reply = running.Runtime.Lifecycle.StartReady cancellation
            match checkedValue reply with
            | Response.PhaseChanged AccessPhase.Ready -> ()
            | _ -> invalidOp "Synthetic authority did not enter ready."
        }

    let private receipt (response: Response) : PairingReceipt =
        match response with
        | Response.PairingCreated value -> value
        | _ -> invalidOp "Synthetic pairing was not created."

    let private credential (response: Response) : SessionCredential =
        match response with
        | Response.SessionIssued value -> value.Credential
        | _ -> invalidOp "Synthetic approved pairing was not exchanged."

    let private creation
        (running: Running) (capabilities: Set<Capability>) (verifier: Verifier) : CreateRequest =
        { InstanceId = running.Identity.InstanceId
          Challenge = Challenge.ofVerifier verifier
          RequestedCapabilities = capabilities
          BrowserOrigins = [ BrowserOrigin.tryParse (running.Audience.GetLeftPart(UriPartial.Authority))
                             |> checkedValue ] }

    let private approve
        (running: Running) (capabilities: Set<Capability>)
        (verifier: Verifier) (pairing: PairingReceipt) : Task<unit> =
        task {
            let request: ApprovalRequest =
                { InstanceId = running.Identity.InstanceId; PairingId = pairing.PairingId
                  UserCode = pairing.UserCode; Challenge = Challenge.ofVerifier verifier
                  Capabilities = capabilities }
            let! reply = running.Runtime.Owner.Approve request cancellation
            match checkedValue reply with
            | Response.PairingApproved _ -> ()
            | _ -> invalidOp "Synthetic pairing was not approved."
        }

    let issue
        (running: Running) (capabilities: Set<Capability>) : Task<SessionCredential> =
        task {
            let verifier = RandomNumberGenerator.GetBytes(32) |> Verifier.ofBytes |> checkedValue
            let! created = running.Runtime.Public.Create (creation running capabilities verifier) cancellation
            let pairing = created |> checkedValue |> receipt
            do! approve running capabilities verifier pairing
            let request: ExchangeRequest =
                { InstanceId = running.Identity.InstanceId; PairingId = pairing.PairingId
                  Verifier = verifier; BrowserOrigin = Some pairing.BrowserOrigin }
            let! exchanged = running.Runtime.Public.Exchange (Ok request) cancellation
            return exchanged |> checkedValue |> credential
        }

    let private apiTarget (audience: Uri) (request: HttpRequestMessage) : bool =
        let methods = Set.ofList [ "GET"; "HEAD"; "POST"; "PUT"; "PATCH"; "DELETE" ]
        request.RequestUri |> Option.ofObj
        |> Option.exists (fun target ->
            target.IsAbsoluteUri && target.UserInfo = ""
            && target.GetLeftPart(UriPartial.Authority) = audience.GetLeftPart(UriPartial.Authority)
            && target.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal)
            && Set.contains request.Method.Method methods)

    type private ApiSessionHandler(audience: Uri, session: SessionCredential) =
        inherit DelegatingHandler(new HttpClientHandler(AllowAutoRedirect = false, UseCookies = false))
        override _.SendAsync(request: HttpRequestMessage, cancellationToken: CancellationToken)
            : Task<HttpResponseMessage> =
            if apiTarget audience request && not (request.Headers.Contains("Authorization")) then
                request.Headers.Authorization <-
                    AuthenticationHeaderValue("Bearer", SessionCredential.text session)
            base.SendAsync(request, cancellationToken)

    let guest (audience: Uri) : HttpClient =
        new HttpClient(
            new HttpClientHandler(AllowAutoRedirect = false, UseCookies = false),
            BaseAddress = audience, Timeout = timeout)

    let client (running: Running) (capabilities: Set<Capability>) : Task<HttpClient> =
        task {
            let! session = issue running capabilities
            return new HttpClient(
                new ApiSessionHandler(running.Audience, session),
                BaseAddress = running.Audience, Timeout = timeout)
        }

    let stop (authority: Authority) : unit =
        if authority.Runtime.Task.IsCompletedSuccessfully then
            authority.Runtime.Task.Result.Lifecycle.Stop()
                .WaitAsync(timeout).GetAwaiter().GetResult()

/// Records server-side reads, rather than assuming that malformed client JSON was unread.
module HttpRequestReadProbe =
    type private ReadingStream(inner: Stream, reads: ConcurrentQueue<unit>) =
        inherit Stream()
        override _.CanRead = inner.CanRead
        override _.CanSeek = inner.CanSeek
        override _.CanWrite = false
        override _.Length = inner.Length
        override _.Position with get () = inner.Position and set value = inner.Position <- value
        override _.Flush() = ()
        override _.Seek(offset: int64, origin: SeekOrigin) = inner.Seek(offset, origin)
        override _.SetLength(_: int64) = raise (NotSupportedException())
        override _.Write(_: byte array, _: int, _: int) = raise (NotSupportedException())
        override _.Read(buffer: byte array, offset: int, count: int) : int =
            reads.Enqueue(())
            inner.Read(buffer, offset, count)
        override _.ReadAsync(buffer: byte array, offset: int, count: int, token: CancellationToken)
            : Task<int> =
            reads.Enqueue(())
            inner.ReadAsync(buffer, offset, count, token)
        override _.ReadAsync(buffer: Memory<byte>, token: CancellationToken) : ValueTask<int> =
            reads.Enqueue(())
            inner.ReadAsync(buffer, token)

    let attach (app: WebApplication) (reads: ConcurrentQueue<unit>) : unit =
        app.Use(Func<HttpContext, RequestDelegate, Task>(fun context next ->
            context.Request.Body <- new ReadingStream(context.Request.Body, reads)
            next.Invoke(context))) |> ignore
