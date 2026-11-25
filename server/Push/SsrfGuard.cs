using System.Net;

namespace TaoOps.Server.Push;

/// <summary>
/// SSRF 防护（FR-A20 验收口径）：目标域名解析的全部 A/AAAA 记录均不得命中
/// 内网段（10/8、172.16/12、192.168/16、127/8、169.254/16、0.0.0.0、::1、
/// fc00::/7、fe80::/10）；IP 字面量直接判定；白名单条目为主机名精确匹配或
/// CIDR 网段，命中即放行。
/// </summary>
public static class SsrfGuard
{
    /// <summary>URL 仅支持 http(s)；返回错误描述，null 表示通过。</summary>
    public static string? ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return "URL must be absolute http(s)";
        }
        if (string.IsNullOrEmpty(uri.Host)) return "URL host is required";
        return null;
    }

    /// <summary>
    /// 完整校验：URL 形态 + DNS 解析全部记录内网判定 + 白名单放行。
    /// resolve 可注入供测试。返回错误描述，null 表示通过。
    /// </summary>
    public static async Task<string?> ValidateTargetAsync(
        string url, IReadOnlyList<string> whitelist, Func<string, Task<IPAddress[]>>? resolve = null)
    {
        var shapeError = ValidateUrl(url);
        if (shapeError is not null) return shapeError;

        var uri = new Uri(url);
        var host = uri.Host;

        // 白名单主机名精确匹配（大小写不敏感）。
        if (whitelist.Any(w => w.Equals(host, StringComparison.OrdinalIgnoreCase))) return null;

        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = resolve is not null
                    ? await resolve(host)
                    : await Dns.GetHostAddressesAsync(host);
            }
            catch (Exception)
            {
                return $"DNS resolution failed for {host}";
            }
            if (addresses.Length == 0) return $"DNS resolution returned no addresses for {host}";
        }

        foreach (var ip in addresses)
        {
            if (IsWhitelisted(ip, whitelist)) continue;
            if (IsPrivateIp(ip)) return $"target {host} resolves to private address {ip}";
        }
        return null;
    }

    /// <summary>内网段判定（PRD FR-A20 清单）。</summary>
    public static bool IsPrivateIp(IPAddress ip)
    {
        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
        var v4 = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
        if (v4.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return InCidr(v4, "0.0.0.0/8") || InCidr(v4, "10.0.0.0/8") || InCidr(v4, "127.0.0.0/8")
                || InCidr(v4, "169.254.0.0/16") || InCidr(v4, "172.16.0.0/12") || InCidr(v4, "192.168.0.0/16");
        }
        // IPv6：未指定地址、环回、fc00::/7（唯一本地）、fe80::/10（链路本地）。
        if (IPAddress.IPv6Loopback.Equals(ip)) return true;
        if (ip.GetAddressBytes().All(b => b == 0)) return true; // ::
        return InCidr(ip, "fc00::/7") || InCidr(ip, "fe80::/10");
    }

    private static bool IsWhitelisted(IPAddress ip, IReadOnlyList<string> whitelist)
    {
        foreach (var entry in whitelist)
        {
            if (IPAddress.TryParse(entry, out var entryIp))
            {
                if (entryIp.Equals(ip) || (entryIp.IsIPv4MappedToIPv6 && entryIp.MapToIPv4().Equals(ip))) return true;
            }
            else if (entry.Contains('/'))
            {
                try
                {
                    if (InCidr(ip, entry)) return true;
                }
                catch (FormatException) { /* 非法条目忽略，配置校验在端点层 */ }
            }
        }
        return false;
    }

    /// <summary>IP 是否落在 CIDR 网段内（IPv4 与 IPv6 通用）。</summary>
    public static bool InCidr(IPAddress ip, string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var prefix)) throw new FormatException($"bad cidr: {cidr}");
        var rangeIp = IPAddress.Parse(parts[0]);
        if (rangeIp.AddressFamily != ip.AddressFamily)
        {
            // 允许 v4 映射形态对 v4 网段。
            if (ip.IsIPv4MappedToIPv6 && rangeIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                return InCidr(ip.MapToIPv4(), cidr);
            return false;
        }
        var ipBytes = ip.GetAddressBytes();
        var rangeBytes = rangeIp.GetAddressBytes();
        if (prefix < 0 || prefix > ipBytes.Length * 8) throw new FormatException($"bad cidr prefix: {cidr}");
        int fullBytes = prefix / 8;
        int remBits = prefix % 8;
        for (var i = 0; i < fullBytes; i++)
            if (ipBytes[i] != rangeBytes[i]) return false;
        if (remBits > 0)
        {
            var mask = (byte)(0xFF << (8 - remBits));
            if ((ipBytes[fullBytes] & mask) != (rangeBytes[fullBytes] & mask)) return false;
        }
        return true;
    }
}
