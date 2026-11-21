using System.Security.Cryptography.X509Certificates;
using TaoOps.Server.Services;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>自签证书管理（M2，契约 §1）：同目录复用同一证书，指纹稳定。</summary>
public class CertManagerTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "taoops-cert-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void GetOrCreate_Twice_SameCertificate()
    {
        var cm = new CertManager(_dataDir);
        using var first = cm.GetOrCreate();
        Assert.True(File.Exists(cm.PfxPath));

        var cm2 = new CertManager(_dataDir);
        using var second = cm2.GetOrCreate();
        Assert.Equal(first.Thumbprint, second.Thumbprint);
    }

    [Fact]
    public void CreateSelfSigned_HasPrivateKey_AndSan()
    {
        using var cert = CertManager.CreateSelfSigned();
        Assert.False(string.IsNullOrEmpty(cert.Thumbprint));
        Assert.True(cert.HasPrivateKey);
        var sanExt = cert.Extensions["2.5.29.17"]; // Subject Alternative Name
        Assert.NotNull(sanExt);
        Assert.Contains("localhost", sanExt.Format(true), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("CN=taoops-server", cert.Subject);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
    }
}
