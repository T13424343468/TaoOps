using System.Text.Json.Serialization;

namespace TaoOps.Server.Waf;

/// <summary>WAF 规则类目，一期覆盖 PRD FR-B1 口径（OWASP Top 10 全类目由类目+自定义规则组合承载）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WafRuleCategory
{
    Sqli,
    Xss,
    Scanner,
    CcRate,
    IpBlacklist,
    IpWhitelist,
    Custom,
}

/// <summary>规则命中后的动作：Deny 拦截、Allow 白名单短路、Log 仅记录。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WafRuleAction
{
    Deny,
    Allow,
    Log,
}

/// <summary>
/// 单条 WAF 规则。规则是数据不是代码（写活纪律，tao-code-standard 一.9）：
/// 匹配语义由生成器落进 Lua 数据文件，改规则不改代码。
/// </summary>
public sealed class WafRule
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public WafRuleCategory Category { get; set; } = WafRuleCategory.Custom;
    public WafRuleAction Action { get; set; } = WafRuleAction.Deny;
    /// <summary>匹配模式：正则（Sqli/Xss/Scanner/Custom）或按类目有专用含义（IP 串、CC 阈值 JSON）。</summary>
    public string Pattern { get; set; } = "";
    /// <summary>CC 类目专用：窗口秒数；其他类目忽略。</summary>
    public int WindowSeconds { get; set; }
    /// <summary>CC 类目专用：窗口内允许的最大请求数。</summary>
    public int MaxRequests { get; set; }
    /// <summary>数值越大优先级越高；生成产物按其降序排列。</summary>
    public int Priority { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>全局设置：WAF 总开关与默认动作。</summary>
public sealed class WafSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>拦截时返回给客户端的 HTTP 状态码。</summary>
    public int DenyStatusCode { get; set; } = 403;
}

/// <summary>规则集快照：规则列表 + 全局设置，存储与生成器的统一输入。</summary>
public sealed class WafRuleSet
{
    public List<WafRule> Rules { get; set; } = [];
    public WafSettings Settings { get; set; } = new();
}
