using System.Buffers.Binary;
using System.Text;

namespace KeyBridge.Core;

public enum MessageKind : byte { Hello = 1, Pair = 2, Ctap = 3, Cancel = 4, Status = 5, Ping = 6, Error = 255 }
public sealed record BridgeFrame(byte Kind, uint RequestId, byte[] Payload);

/// <summary>Version one framing, always inside the authenticated TLS stream.</summary>
public static class BridgeProtocol
{
    public const int HeaderSize = 16;
    public const int MaximumPayload = 65536;
    public const int MaximumCtap = 7609;
    public static async Task<BridgeFrame> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        byte[] header = new byte[HeaderSize];
        await stream.ReadExactlyAsync(header, cancellationToken);
        if (!header.AsSpan(0, 4).SequenceEqual("KBRG"u8) || header[4] != 1 || header[6] != 0 || header[7] != 0)
            throw new InvalidDataException("Unsupported KeyBridge frame header.");
        uint size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12));
        if (size > MaximumPayload) throw new InvalidDataException("KeyBridge frame exceeds the size limit.");
        uint id = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8));
        if (id == 0) throw new InvalidDataException("Request ID zero is reserved.");
        byte[] payload = new byte[(int)size];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return new(header[5], id, payload);
    }
    public static async Task WriteAsync(Stream stream, BridgeFrame frame, CancellationToken cancellationToken = default)
    {
        if (frame.RequestId == 0 || frame.Payload.Length > MaximumPayload) throw new InvalidDataException("Invalid outgoing frame.");
        byte[] header = new byte[HeaderSize];
        "KBRG"u8.CopyTo(header);
        header[4] = 1;
        header[5] = frame.Kind;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), frame.RequestId);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), (uint)frame.Payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(frame.Payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
    public static string PairingCode(byte[] phoneCertificate, byte[] desktopCertificate)
    {
        byte[] prefix = Encoding.UTF8.GetBytes("KeyBridge pairing v1");
        byte[] combined = new byte[prefix.Length + phoneCertificate.Length + desktopCertificate.Length];
        prefix.CopyTo(combined, 0);
        phoneCertificate.CopyTo(combined, prefix.Length);
        desktopCertificate.CopyTo(combined, prefix.Length + phoneCertificate.Length);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(combined));
    }
}
