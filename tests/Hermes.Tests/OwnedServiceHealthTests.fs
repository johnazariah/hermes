module Hermes.Tests.OwnedServiceHealthTests

open System
open System.Collections.Generic
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open Hermes.Tests.OwnedServiceProcess

let private ownedPid = 4242

type private ProbeHandler(steps: (CancellationToken -> Task<HttpResponseMessage>) list) =
    inherit HttpMessageHandler()
    let remaining = Queue<_>(steps)

    override _.SendAsync(request, token) =
        let uri =
            request.RequestUri
            |> Option.ofObj
            |> Option.defaultWith (fun () -> invalidOp "Health probe URI is missing")
        Assert.Equal(HttpMethod.Get, request.Method)
        Assert.Equal("/health", uri.AbsolutePath)
        Assert.NotEmpty(remaining)
        remaining.Dequeue() token

    member _.VerifyComplete() = Assert.Empty(remaining)

let private withProbes steps verify =
    task {
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 20.0)
        use handler = new ProbeHandler(steps deadline)
        use client =
            new HttpClient(handler, BaseAddress = Uri("http://owned.invalid"),
                           Timeout = TimeSpan.FromSeconds 2.0)
        do! verify client deadline.Token
        handler.VerifyComplete()
    }

let private ready (processId: int) (_: CancellationToken) =
    JsonSerializer.Serialize(
        {| status = "healthy"; service = "hermes"; processId = processId |})
    |> EmailProviderTransportFixtures.ok

let private timedOut (_: CancellationToken) =
    Task.FromException<HttpResponseMessage>(
        TaskCanceledException("Synthetic per-probe timeout", TimeoutException()))

let private poll client token =
    waitForHealth client ownedPid (fun () -> false) token

[<Fact>]
let ``Per-probe timeout retries until matching owned health within the startup budget`` () =
    withProbes
        (fun _ -> [ timedOut; ready ownedPid ])
        poll

let private cancelProbe (deadline: CancellationTokenSource) (token: CancellationToken) =
    deadline.Cancel()
    Task.FromCanceled<HttpResponseMessage>(token)

let private assertCancelled (token: CancellationToken) (work: unit -> Task<'value>) =
    task {
        let! error =
            Assert.ThrowsAnyAsync<OperationCanceledException>(
                Func<Task>(fun () -> work () :> Task))
        Assert.True(token.IsCancellationRequested)
        Assert.Equal(token, error.CancellationToken)
    }

let private assertDeadline client token =
    task {
        do! assertCancelled token (fun () -> checkHealth client ownedPid token)
        do! assertCancelled token (fun () -> poll client token)
    }

[<Fact>]
let ``Overall cancellation during a probe propagates and prevents further polling`` () =
    withProbes
        (fun deadline -> [ cancelProbe deadline ])
        assertDeadline

let private assertWrongPid client token =
    task {
        let! error =
            Assert.ThrowsAsync<Xunit.Sdk.EqualException>(
                Func<Task>(fun () -> poll client token :> Task))
        Assert.Contains(string ownedPid, error.Message)
        Assert.Contains(string (ownedPid + 1), error.Message)
    }

[<Fact>]
let ``Healthy response from another PID fails instead of being masked or retried`` () =
    withProbes
        (fun _ -> [ ready (ownedPid + 1) ])
        assertWrongPid

let private earlyDiagnostic output errors =
    earlyExitDiagnostic { Code = 78; Output = output; Errors = errors }

[<Fact>]
let ``Early child diagnostics retain exit and both captures while redacting authority`` () =
    let credential = "synthetic-bearer-value"
    let authorities =
        [ "h1.synthetic.token"; String.replicate 43 "v"; "ABCD-EFGH-JKMN" ]
    let output = String.concat " " ("output captured" :: authorities)
    let message =
        earlyDiagnostic (output + " Bearer " + credential)
            ("error captured bEaReR " + credential)
    Assert.Contains("exit code 78", message)
    Assert.Contains("output captured", message)
    Assert.Contains("error captured", message)
    Assert.Contains("[redacted]", message)
    credential :: authorities
    |> List.iter (fun authority -> Assert.DoesNotContain(authority, message))

[<Fact>]
let ``Early child diagnostics bound both captures without leaking a truncated proof`` () =
    let streamLimit = (4096 - (earlyDiagnostic "" "").Length) / 2
    let prefix = "stdout retained "
    let output =
        prefix + String.replicate (streamLimit - prefix.Length - 30) "."
        + String.replicate 43 "S" + "." + String.replicate 4096 "x"
    let message =
        earlyDiagnostic output ("stderr retained " + String.replicate 4096 "y")
    Assert.InRange(message.Length, 1, 4096)
    Assert.Contains(prefix, message)
    Assert.Contains("stderr retained ", message)
    Assert.Contains("[truncated]", message)
    Assert.DoesNotContain(String.replicate 8 "S", message)
