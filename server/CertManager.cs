using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TaoOps.Server.Services;

/// <summary>
/// 中控自签证书管理：首次启动签发长期证书（RSA-2048，10 年，SAN localhost/127.0.0.1），
/// PFX 落数据目录，重启复用（契约 §1：自托管无服务器，不依赖外部 CA）。
/// </summary>
public class CertManager
{
    private readonly string _pfxPath;
    private const string PfxPassword = "taoops-local";

    public CertManager(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _pfxPath = Path.Combine(dataDir, "server.pfx");
    }

    /// <summary>加载或创建证书；同一数据目录返回同一证书（按指纹断言）。</summary>
    public X509Certificate2 GetOrCreate()
    {
        if (File.Exists(_pfxPath))
            return X509CertificateLoader.LoadPkcs12FromFile(_pfxPath, PfxPassword);

        var cert = CreateSelfSigned();
        File.WriteAllBytes(_pfxPath, cert.Export(X509ContentType.Pkcs12, PfxPassword));
        return cert;
    }

    /// <summary>PFX 文件路径（E2E 断言与运维排查用）。</summary>
    public string PfxPath => _pfxPath;

    internal static X509Certificate2 CreateSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=taoops-server", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));

        var now = DateTimeOffset.UtcNow.AddDays(-1);
        // X509Certificate2 持私钥：用 PFX 往返确保副本带私钥（loader 口径，规避 SYSLIB0057）。
        var cert = req.CreateSelfSigned(now, now.AddYears(10));
        var pfx = cert.Export(X509ContentType.Pkcs12, PfxPassword);
        return X509CertificateLoader.LoadPkcs12(pfx, PfxPassword);
    }
}
