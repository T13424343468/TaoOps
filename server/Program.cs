using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Text.Json;
using TaoOps.Server.Services;
using TaoOps.Server.Waf;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddGrpc();
builder.Services.AddSingleton<NodeRegistry>();
builder.Services.AddSingleton<NodeCommandHub>();
builder.Services.AddSingleton<NodeStreamHub>();

// WAF 管理面（M3）：规则真源与事件账本落数据目录。
var wafDataDir = Environment.GetEnvironmentVariable("TAOOPS_DATA_DIR") ?? "/var/lib/taoops/server";
builder.Services.AddSingleton<WafRuleStore>(_ => new WafRuleStore(wafDataDir));
builder.Services.AddSingleton<WafEventStore>(_ => new WafEventStore(wafDataDir));
builder.Services.AddSingleton<WafEventIngestor>();

// 传输模式（契约 §1）：TAOOPS_INSECURE=1 走明文 h2c（仅本地回环测试）；
// 默认 TLS，中控自签证书落数据目录，重启复用。
var insecure = Environment.GetEnvironmentVariable("TAOOPS_INSECURE") == "1";
var legacyUrls = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"));
if (!insecure)
{
    var dataDir = Environment.GetEnvironmentVariable("TAOOPS_DATA_DIR") ?? "/var/lib/taoops/server";
    var port = int.TryParse(Environment.GetEnvironmentVariable("TAOOPS_PORT"), out var p) && p > 0 ? p : 5241;
    var cert = new CertManager(dataDir).GetOrCreate();
    builder.WebHost.ConfigureKestrel(o =>
        o.ListenAnyIP(port, l =>
        {
            l.Protocols = HttpProtocols.Http2 | HttpProtocols.Http1;
            l.UseHttps(cert);
        }));
}
else if (!legacyUrls)
{
    var port = int.TryParse(Environment.GetEnvironmentVariable("TAOOPS_PORT"), out var p) && p > 0 ? p : 5241;
    builder.WebHost.ConfigureKestrel(o =>
        o.ListenAnyIP(port, l => l.Protocols = HttpProtocols.Http2));
}
// insecure + legacyUrls：沿用 --urls 明文端点（M1 E2E 兼容路径）。

var app = builder.Build();
app.UseWebSockets();
app.MapGrpcService<AgentService>();
app.MapGet("/", () => insecure ? "TaoOps Server M2 (insecure)" : "TaoOps Server M2");

// WebSocket 桥接（契约 §1）：终端 /ws/agent/{node}/terminal[?session=]、
// 日志 /ws/agent/{node}/logs?path=&tail=
app.Map("/ws/agent/{nodeId}/terminal", async (HttpContext ctx, NodeStreamHub streams) =>
{
    await WsBridge.MapTerminal(ctx, streams, ctx.GetRouteValue("nodeId")!.ToString()!,
        ctx.Request.Query["session"].Count > 0 ? ctx.Request.Query["session"][0] : null);
});
app.Map("/ws/agent/{nodeId}/logs", async (HttpContext ctx, NodeStreamHub streams) =>
{
    var path = ctx.Request.Query["path"].Count > 0 ? ctx.Request.Query["path"][0]! : "";
    var tail = int.TryParse(ctx.Request.Query["tail"], out var t) ? t : 0;
    await WsBridge.MapLogs(ctx, streams, ctx.GetRouteValue("nodeId")!.ToString()!, path, tail);
});

// ---- WAF 管理 API（M3，PRD FR-B1）----
app.MapGet("/api/waf/rules", (WafRuleStore store) => Results.Json(store.Load()));
app.MapPost("/api/waf/rules", (WafRule rule, WafRuleStore store) =>
{
    try { return Results.Json(store.Add(rule)); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPut("/api/waf/rules/{id}", (string id, WafRule rule, WafRuleStore store) =>
{
    rule.Id = id;
    return store.Update(rule) ? Results.Ok() : Results.NotFound();
});
app.MapDelete("/api/waf/rules/{id}", (string id, WafRuleStore store) =>
    store.Remove(id) ? Results.Ok() : Results.NotFound());
app.MapPatch("/api/waf/rules/{id}/enabled", (string id, bool enabled, WafRuleStore store) =>
    store.SetEnabled(id, enabled) ? Results.Ok() : Results.NotFound());
app.MapGet("/api/waf/settings", (WafRuleStore store) => Results.Json(store.Load().Settings));
app.MapPut("/api/waf/settings", (WafSettings settings, WafRuleStore store) =>
{
    store.SaveSettings(settings);
    return Results.Ok();
});
app.MapGet("/api/waf/events", (WafEventStore store, string? nodeId, long? from, long? to) =>
    Results.Json(store.Query(nodeId, from, to)));
app.MapPost("/api/waf/rules/import", async (HttpRequest req, WafRuleStore store) =>
{
    try
    {
        using var reader = new StreamReader(req.Body);
        store.Import(await reader.ReadToEndAsync());
        return Results.Ok();
    }
    catch (JsonException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapGet("/api/waf/rules/export", (WafRuleStore store) => Results.Text(store.Export(), "application/json"));
// 产物预览：管理员部署前先看生成结果（真机装配步骤见待真机清单）。
app.MapGet("/api/waf/config/preview", (WafRuleStore store) =>
{
    var bundle = OpenRestyConfigGenerator.Generate(store.Load());
    return Results.Json(new { bundle.NginxConf, bundle.SiteConf, bundle.AccessLua, bundle.RulesJson });
});

app.Run();
