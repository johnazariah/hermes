using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Hermes.Tray.Access;

namespace Hermes.Tray;

internal sealed record PublicAccessIdentity(
    string InstanceId, OwnerProcessIdentity Process, string Origin);

internal interface IPublicAccessIdentity : IDisposable
{
    Task<OwnerResult<PublicAccessIdentity>> ReadAsync(CancellationToken cancellationToken);
}

internal sealed class HttpAccessIdentity : IPublicAccessIdentity
{
    private readonly OwnerTarget _target;
    private readonly HttpClient _http;

    public HttpAccessIdentity(OwnerTarget target)
        : this(target, new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(2)
        })) { }

    internal HttpAccessIdentity(OwnerTarget target, HttpClient http)
    {
        (_target, _http) = (target, http);
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<OwnerResult<PublicAccessIdentity>> ReadAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var identity = await ReadIdentityAsync(deadline.Token);
            cancellationToken.ThrowIfCancellationRequested();
            return new OwnerResult<PublicAccessIdentity>.Success(identity);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new OwnerResult<PublicAccessIdentity>.Failure(OwnerFailure.Timeout);
        }
        catch (AccessProtocolFault error)
        {
            return new OwnerResult<PublicAccessIdentity>.Failure(error.Failure);
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            return new OwnerResult<PublicAccessIdentity>.Failure(OwnerFailure.Unavailable);
        }
    }

    private async Task<PublicAccessIdentity> ReadIdentityAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _target.ApiOrigin + "/api/access");
        request.Headers.CacheControl = new() { NoCache = true, NoStore = true };
        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK ||
            response.Content.Headers.ContentType?.MediaType != "application/json")
            throw new AccessProtocolFault(OwnerFailure.Unavailable);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var bytes = await ReadBoundedAsync(stream, cancellationToken);
        return AccessJson.Read(bytes, ReadAnonymousIdentity);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[4097];
        var length = 0;
        while (length < bytes.Length &&
            await stream.ReadAsync(bytes.AsMemory(length), cancellationToken) is > 0 and var count)
            length += count;
        return length is > 0 and <= 4096
            ? bytes[..length] : throw new AccessProtocolFault(OwnerFailure.Protocol);
    }

    private static PublicAccessIdentity ReadAnonymousIdentity(JsonElement root)
    {
        AccessJson.Object(root, "version", "instanceId", "processId", "processStartTimeUtc",
            "origin", "state", "capabilities", "expiresAt", "pairingMethod");
        var capabilities = AccessJson.Capabilities(root, "capabilities", true);
        if (AccessJson.Integer(root, "version") != 1 ||
            AccessJson.Integer(root, "processId") <= 0 ||
            AccessJson.Text(root, "state") != "anonymous" ||
            AccessJson.Text(root, "pairingMethod") != "ownerApproval" ||
            root.GetProperty("expiresAt").ValueKind != JsonValueKind.Null ||
            !AccessCapabilities.Equal(capabilities, ImmutableArray.Create(AccessCapability.ArchiveRead)))
            throw new AccessProtocolFault(OwnerFailure.Protocol);
        return new(AccessJson.Identifier(root, "instanceId"),
            new(AccessJson.Integer(root, "processId"), AccessJson.Timestamp(root, "processStartTimeUtc")),
            AccessJson.Text(root, "origin"));
    }

    public void Dispose() => _http.Dispose();
}
