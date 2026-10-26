namespace KeyBridge.Usb;

/// <summary>The only authenticator boundary: Windows forwards opaque CTAP requests to Android.</summary>
public interface ICtapTransport
{
    Task<byte[]> ExchangeAsync(byte[] request, CancellationToken cancellationToken);
}
