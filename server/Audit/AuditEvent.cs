using System.Security.Cryptography;
using System.Text.Json;

namespace TaoOps.Server.Audit;

/// <summary>
/// 一条审计留痕（FR-D4：所有写操作可追溯到人）。
/// 哈希链防篡改：Hash = SHA256(PrevHash + 本条规范字段串)，篡改任一历史条目即断链可检出。
/// </summary>
public sealed class AuditEvent
{
    public long Seq { get; set; }
    public long Ts { get; set; }
    public string User { get; set; } = "";
    public string Role { get; set; } = "";
    /// <summary>动作：HTTP 方法（中间件留痕）或 login/logout 等语义动作。</summary>
    public string Action { get; set; } = "";
    /// <summary>对象：请求路径或操作目标。</summary>
    public string Target { get; set; } = "";
    public string Ip { get; set; } = "";
    /// <summary>结果：HTTP 状态码或 ok/denied。</summary>
    public string Result { get; set; } = "";
    public string PrevHash { get; set; } = "";
    public string Hash { get; set; } = "";

    /// <summary>规范字段串（哈希输入，字段顺序即契约，改序=全链重算）。</summary>
    public string Canonical() =>
        string.Join('|', Seq, Ts, User, Role, Action, Target, Ip, Result, PrevHash);

    public void ComputeHash() =>
        Hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Canonical()))).ToLowerInvariant();
}
