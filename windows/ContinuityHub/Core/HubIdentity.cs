using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ContinuityHub.Core;

/// <summary>The hub's self-signed TLS identity. Its SHA-256 fingerprint is what Android pins.</summary>
public sealed class HubIdentity : IDisposable
{
    private const string FriendlyName = "Continuity Hub (local pairing)";

    public X509Certificate2 Certificate { get; }
    public string Fingerprint { get; }

    private HubIdentity(X509Certificate2 cert)
    {
        Certificate = cert;
        Fingerprint = FingerprintOf(cert);
    }

    public static string FingerprintOf(X509Certificate cert) =>
        Convert.ToHexStringLower(SHA256.HashData(cert.GetRawCertData()));

    /// <summary>
    /// Loads the cert from CurrentUser\My by thumbprint, or creates one there on first launch.
    /// The private key is stored by Windows (DPAPI-protected, per-user, non-exportable).
    /// </summary>
    public static HubIdentity LoadOrCreate(HubConfig config)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);

        if (!string.IsNullOrEmpty(config.CertThumbprint))
        {
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, config.CertThumbprint, validOnly: false);
            foreach (var c in found)
                if (c.HasPrivateKey && c.NotAfter > DateTime.Now.AddDays(30))
                    return new HubIdentity(c);
        }

        using var created = CreateSelfSigned();
        // Re-import so the key is persisted (SChannel can't serve TLS with an ephemeral key).
        var persisted = X509CertificateLoader.LoadPkcs12(
            created.Export(X509ContentType.Pfx), password: null,
            X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);
        persisted.FriendlyName = FriendlyName;
        store.Add(persisted);

        config.CertThumbprint = persisted.Thumbprint;
        config.Save();
        Log.Info($"Created hub certificate {persisted.Thumbprint}");
        return new HubIdentity(persisted);
    }

    /// <summary>For tests: a cert whose key lives only until disposal.</summary>
    public static HubIdentity CreateTransient()
    {
        using var created = CreateSelfSigned();
        return new HubIdentity(X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null));
    }

    internal static X509Certificate2 CreateSelfSigned(string cn = "Continuity Hub")
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // serverAuth
        var now = DateTimeOffset.UtcNow;
        return req.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));
    }

    public void Dispose() => Certificate.Dispose();
}
