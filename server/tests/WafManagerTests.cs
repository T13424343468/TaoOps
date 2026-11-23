using System.IO;
using TaoOps.Server.Waf;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>断言清单正源：docs/breakpoint-m3-waf.md A1–A5（生成器）。</summary>
public class OpenRestyConfigGeneratorTests
{
    private static WafRule Rule(string id, WafRuleCategory cat, WafRuleAction action,
        string pattern, int priority = 0, bool enabled = true,
        int window = 0, int maxRequests = 0) => new()
    {
        Id = id, Name = $"rule-{id}", Category = cat, Action = action,
        Pattern = pattern, Priority = priority, Enabled = enabled,
        WindowSeconds = window, MaxRequests = maxRequests,
    };

    [Fact]
    public void EmptyRules_StillEmitsAccessDirective_AndEmptyJsonArray()
    {
        // A1：空规则集也要有完整装配骨架，全局开关体现在产物里。
        var bundle = OpenRestyConfigGenerator.Generate(new WafRuleSet());
        Assert.Contains("access_by_lua_file", bundle.SiteConf);
        Assert.Equal("[]", bundle.RulesJson);
        Assert.Contains("TAOOPS_WAF_ENABLED = true", bundle.NginxConf);
    }

    [Fact]
    public void GlobalOff_IsReflectedInGeneratedConf()
    {
        var bundle = OpenRestyConfigGenerator.Generate(new WafRuleSet
        {
            Settings = new WafSettings { Enabled = false },
        });
        Assert.Contains("TAOOPS_WAF_ENABLED = false", bundle.NginxConf);
    }

    [Fact]
    public void MixedCategories_AllPresent_OrderedByPriorityDesc()
    {
        // A2：SQLi/XSS/CC/扫描器/IP 黑白名单全类目，Priority 降序进产物。
        var set = new WafRuleSet
        {
            Rules =
            [
                Rule("s1", WafRuleCategory.Sqli, WafRuleAction.Deny, "(union\\s+select)", priority: 10),
                Rule("x1", WafRuleCategory.Xss, WafRuleAction.Deny, "<script", priority: 20),
                Rule("c1", WafRuleCategory.CcRate, WafRuleAction.Deny, "", priority: 30, window: 60, maxRequests: 120),
                Rule("sc1", WafRuleCategory.Scanner, WafRuleAction.Log, "(sqlmap|nikto)", priority: 40),
                Rule("b1", WafRuleCategory.IpBlacklist, WafRuleAction.Deny, "203.0.113.9", priority: 50),
                Rule("w1", WafRuleCategory.IpWhitelist, WafRuleAction.Allow, "198.51.100.7", priority: 60),
            ],
        };
        var bundle = OpenRestyConfigGenerator.Generate(set);

        // 优先级降序：w1(60) 在最前，s1(10) 在最后。
        var wPos = bundle.RulesJson.IndexOf("\"w1\"", StringComparison.Ordinal);
        var sPos = bundle.RulesJson.IndexOf("\"s1\"", StringComparison.Ordinal);
        Assert.True(wPos >= 0 && sPos > wPos, $"expect w1 before s1, got {bundle.RulesJson}");
        foreach (var id in new[] { "s1", "x1", "c1", "sc1", "b1", "w1" })
            Assert.Contains($"\"{id}\"", bundle.RulesJson);
        // 正则原样进数据文件（JSON 转义后 \s 记为 \\s，语义不变）。
        Assert.Contains("union", bundle.RulesJson);
        Assert.Contains("\"windowSeconds\":60", bundle.RulesJson);
        Assert.Contains("\"maxRequests\":120", bundle.RulesJson);
    }

    [Fact]
    public void DisabledRule_ExcludedFromAllArtifacts()
    {
        // A3：停用规则不进任何产物。
        var set = new WafRuleSet
        {
            Rules = [Rule("on1", WafRuleCategory.Sqli, WafRuleAction.Deny, "a", enabled: true),
                     Rule("off1", WafRuleCategory.Sqli, WafRuleAction.Deny, "b", enabled: false)],
        };
        var bundle = OpenRestyConfigGenerator.Generate(set);
        Assert.Contains("\"on1\"", bundle.RulesJson);
        Assert.DoesNotContain("off1", bundle.RulesJson);
    }

    [Fact]
    public void AllowAction_PresentInRuleData_ForWhitelistShortCircuit()
    {
        // A4：白名单动作进数据文件，Lua 模板固定先 Allow 后 Deny。
        var set = new WafRuleSet
        {
            Rules = [Rule("w1", WafRuleCategory.IpWhitelist, WafRuleAction.Allow, "198.51.100.7")],
        };
        var bundle = OpenRestyConfigGenerator.Generate(set);
        Assert.Contains("\"action\":\"Allow\"", bundle.RulesJson);
        // Lua 检查顺序：Allow 短路分支先于 Deny 分支出现。
        Assert.True(bundle.AccessLua.IndexOf("if rule.action == \"Allow\"", StringComparison.Ordinal)
            < bundle.AccessLua.IndexOf("if rule.action == \"Deny\"", StringComparison.Ordinal));
    }

    [Fact]
    public void SameInput_ProducesByteIdenticalOutput()
    {
        // A5：确定性——同输入逐字节相同。
        var set = new WafRuleSet
        {
            Rules =
            [
                Rule("a", WafRuleCategory.Sqli, WafRuleAction.Deny, "p1", priority: 5),
                Rule("b", WafRuleCategory.Xss, WafRuleAction.Log, "p2", priority: 5), // 同优先级按 Id 稳定排序
            ],
        };
        var first = OpenRestyConfigGenerator.Generate(set);
        var second = OpenRestyConfigGenerator.Generate(set);
        Assert.Equal(first.NginxConf, second.NginxConf);
        Assert.Equal(first.AccessLua, second.AccessLua);
        Assert.Equal(first.RulesJson, second.RulesJson);
    }

    [Fact]
    public void AccessLua_DecodesUriBeforeRegexMatch()
    {
        // A3（本班断言）：URI 先 ngx.unescape_uri 再匹配——%20 编码不得绕过 SQLi 规则；
        // 日志仍记原始 URI（取证口径）。
        var set = new WafRuleSet { Rules = [Rule("s1", WafRuleCategory.Sqli, WafRuleAction.Deny, "union")] };
        var lua = OpenRestyConfigGenerator.Generate(set).AccessLua;
        Assert.Contains("ngx.unescape_uri", lua);
        Assert.Contains("pcall(ngx.unescape_uri", lua);
        // 原始 URI 仅进日志行，不进匹配 subject。
        Assert.Matches(@"local subject = decoded ..", lua);
    }

    [Fact]
    public void AccessLua_IsInlineHandler_NotReturnedClosure()
    {
        // A2（本班断言，实弹班 ed407e3 同款）：access_by_lua_file 把文件体当 handler 直接执行，
        // return 闭包永远不会被调用；产物必须是 do-block 内联执行体。
        var set = new WafRuleSet { Rules = [Rule("s1", WafRuleCategory.Sqli, WafRuleAction.Deny, "p")] };
        var lua = OpenRestyConfigGenerator.Generate(set).AccessLua;
        Assert.DoesNotContain("return function()", lua);
        Assert.Contains("do", lua);
        // ngx.re 走全局表（974cc3a 同款）：require('ngx.re') 无 find。
        Assert.DoesNotContain("require(\"ngx.re\")", lua);
        Assert.Contains("local ngx_re = ngx.re", lua);
    }
}

/// <summary>断言清单 A6/A9：规则存储持久化、原子写、导入导出。</summary>
public class WafRuleStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "taoops-wafstore-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Crud_PersistsAcrossStoreReload()
    {
        var store = new WafRuleStore(_dir);
        var added = store.Add(new WafRule
        {
            Id = "r1", Name = "block-sqli", Category = WafRuleCategory.Sqli,
            Action = WafRuleAction.Deny, Pattern = "(union select)", Priority = 10,
        });
        Assert.Equal("r1", added.Id);

        var reloaded = new WafRuleStore(_dir).Load();
        var rule = Assert.Single(reloaded.Rules);
        Assert.Equal("block-sqli", rule.Name);
        Assert.Equal(WafRuleCategory.Sqli, rule.Category);
        Assert.True(rule.Enabled);
    }

    [Fact]
    public void Save_LeavesNoTmpResidue()
    {
        // A6：tmp+move 原子替换，写后无 .tmp 残留。
        var store = new WafRuleStore(_dir);
        store.Add(new WafRule { Id = "r1", Name = "n", Pattern = "p" });
        Assert.DoesNotContain(Directory.GetFiles(Path.Combine(_dir, "waf")), f => f.EndsWith(".tmp"));
    }

    [Fact]
    public void AddDuplicateId_Throws()
    {
        var store = new WafRuleStore(_dir);
        store.Add(new WafRule { Id = "dup", Name = "n", Pattern = "p" });
        Assert.Throws<InvalidOperationException>(
            () => store.Add(new WafRule { Id = "dup", Name = "n2", Pattern = "p2" }));
    }

    [Fact]
    public void SetEnabled_TogglesWithoutTouchingOtherFields()
    {
        var store = new WafRuleStore(_dir);
        store.Add(new WafRule { Id = "r1", Name = "n", Pattern = "p", Priority = 7 });
        Assert.True(store.SetEnabled("r1", false));
        var rule = new WafRuleStore(_dir).Load().Rules.Single();
        Assert.False(rule.Enabled);
        Assert.Equal(7, rule.Priority);
        Assert.False(store.SetEnabled("missing", true));
    }

    [Fact]
    public void ExportImport_RoundtripPreservesRules()
    {
        // A9：导出→导入 roundtrip 一致。
        var store = new WafRuleStore(_dir);
        store.Add(new WafRule
        {
            Id = "r1", Name = "cc", Category = WafRuleCategory.CcRate,
            Action = WafRuleAction.Deny, WindowSeconds = 60, MaxRequests = 100, Priority = 3,
        });
        var json = store.Export();

        var otherDir = Path.Combine(_dir, "imported");
        var other = new WafRuleStore(otherDir);
        other.Import(json);
        var original = store.Load();
        var imported = other.Load();
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(original),
            System.Text.Json.JsonSerializer.Serialize(imported));
    }

    [Fact]
    public void Import_BadJson_Throws()
    {
        var store = new WafRuleStore(_dir);
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => store.Import("{not json"));
    }
}
