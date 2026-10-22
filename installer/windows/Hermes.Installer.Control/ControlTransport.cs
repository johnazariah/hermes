using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Hermes.Servicing;

namespace Hermes.Installer.Control;

internal sealed record ControlPaths(string DirectoryPath, string GatePath,
    string EndpointPath, string ReceiptPath)
{
    [SupportedOSPlatform("windows")]
    internal static ControlPaths Current()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(profile))
            throw new InvalidOperationException("The user profile is unavailable.");
        var directory = Path.Combine(profile, "Hermes", "Servicing",
            HermesServicingProtocol.UpgradeCode.ToString("D"));
        return new(directory, Path.Combine(directory, "admission.bin"),
            Path.Combine(directory, "control.json"), Path.Combine(directory, "call-receipt.json"));
    }
}

internal static class ControlFiles
{
    internal static ControlEndpoint ReadEndpoint(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length is <= 0 or > ControlNames.MaximumPacket)
            throw new InvalidDataException("Invalid control descriptor.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return JsonSerializer.Deserialize(bytes, ControlJson.Default.ControlEndpoint)
            ?? throw new InvalidDataException("Missing control descriptor.");
    }

    internal static void PublishEndpoint(string path, ControlEndpoint endpoint) =>
        Publish(path, JsonSerializer.SerializeToUtf8Bytes(endpoint,
            ControlJson.Default.ControlEndpoint));

    internal static void PublishReceipt(string path, InstallerCallReceipt receipt) =>
        Publish(path, JsonSerializer.SerializeToUtf8Bytes(receipt,
            ControlJson.Default.InstallerCallReceipt));

    private static void Publish(string path, byte[] bytes)
    {
        if (bytes.Length > ControlNames.MaximumPacket)
            throw new InvalidDataException("Control record is too large.");
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal static class ControlPackets
{
    internal static async Task<T> ReadAsync<T>(
        Stream stream, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is 0 or > ControlNames.MaximumPacket)
            throw new InvalidDataException("Invalid control packet.");
        var body = new byte[(int)length];
        await stream.ReadExactlyAsync(body, cancellationToken);
        return JsonSerializer.Deserialize(body, type)
            ?? throw new InvalidDataException("Missing control packet.");
    }

    internal static async Task WriteAsync<T>(
        Stream stream, T value, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value, type);
        if (body.Length > ControlNames.MaximumPacket)
            throw new InvalidDataException("Control packet is too large.");
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, checked((uint)body.Length));
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
