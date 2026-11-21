using Microsoft.AspNetCore.Server.Kestrel.Core;
using TaoOps.Server.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddGrpc();
builder.Services.AddSingleton<NodeRegistry>();
builder.Services.AddSingleton<NodeCommandHub>();
builder.Services.AddSingleton<NodeStreamHub>();

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

app.Run();
