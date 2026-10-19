module Hermes.Tests.OutlookSparseMappingTests

open System.Net.Http
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.EmailProviderTransportFixtures

let private emptyMessage: Domain.EmailMessage =
    { ProviderId = ""
      ThreadId = ""
      Sender = None
      Subject = None
      Date = None
      Labels = []
      HasAttachments = false
      BodyText = None }

let private withMessage (messageJson: string)
    (verify: Algebra.EmailProvider -> Task<unit>) : Task<unit> =
    let handler (request: HttpRequestMessage) =
        if isPage request then ok ("{\"value\":[" + messageJson + "]}")
        else ok messageJson
    task {
        let! transport = create false TestHelpers.silentLogger handler
        try
            do! verify transport.Provider
        finally
            transport.Dispose()
    }

[<Theory>]
[<InlineData("""{}""")>]
[<InlineData("""{"bodyPreview":"   ","body":{"contentType":"text","content":"   "},"flag":{}}""")>]
[<InlineData("""{"bodyPreview":null,"body":{"contentType":"text","content":null}}""")>]
[<Trait("Category", "Integration")>]
let ``Outlook sparse full and preview mappings preserve absent identity and empty content``
    (messageJson: string) =
    withMessage messageJson (fun provider ->
        task {
            let! full = provider.getFullMessage "missing-id"
            Assert.Equal<Domain.EmailMessage>(emptyMessage, full)
            let! legacy = provider.listNewMessages None
            Assert.Equal<Domain.EmailMessage list>([ emptyMessage ], legacy)
            let! page = provider.listMessagePage None None 1
            Assert.Equal<Domain.EmailMessage list>([ emptyMessage ], page.Messages)
            Assert.True(page.NextPageToken.IsNone)
            Assert.Equal(1L, page.ResultSizeEstimate)
        })
