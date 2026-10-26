using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace KeyBridge.Core;

/// <summary>Transport identity only. FIDO credential private keys never exist on Windows.</summary>
public static class HostIdentity
{
    private const string FriendlyName = "KeyBridge local transport identity v1";
    public static X509Certificate2 LoadOrCreate()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        var existing = store.Certificates.Cast<X509Certificate2>().FirstOrDefault(c =>
            c.FriendlyName == FriendlyName && c.HasPrivateKey && c.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(1));
        if (existing is not null) return existing;
        string keyName = "KeyBridge.Transport." + Guid.NewGuid().ToString("N");
        using var key = CngKey.Create(CngAlgorithm.ECDsaP256, keyName, new CngKeyCreationParameters {
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
            KeyUsage = CngKeyUsages.Signing,
            ExportPolicy = CngExportPolicies.None
        });
        using var ecdsa = new ECDsaCng(key);
        var request = new CertificateRequest("CN=KeyBridge Desktop", ecdsa, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var purposes = new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(purposes, false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(3));
        certificate.FriendlyName = FriendlyName;
        store.Add(certificate);
        // Use the persisted certificate/provider association, not the transient request's key context.
        return store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, false)[0];
    }
    public static string Fingerprint(X509Certificate2 certificate) => Convert.ToHexString(SHA256.HashData(certificate.RawData));
}
