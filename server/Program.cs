using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Text.Json;
using TaoOps.Server.Alerts;
using TaoOps.Server.Audit;
using TaoOps.Server.Auth;
using Push = TaoOps.Server.Push;
using TaoOps.Server.Relay;
using TaoOps.Server.Sec;
using TaoOps.Server.Services;
using TaoOps.Server.Waf;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddGrpc();
builder.Services.AddSingleton<NodeRegistry>();
builder.Services.AddSingleton<NodeCommandHub>();
builder.Services.AddSingleton<NodeStreamHub>();
builder.Services.AddSingleton<NodeCommandBridge>();

// WAF 管理面（M3）：规则真源与事件账本落数据目录。
var wafDataDir = Environment.GetEnvironmentVariable("TAOOPS_DATA_DIR") ?? "/var/lib/taoops/server";
builder.Services.AddSingleton<WafRuleStore>(_ => new WafRuleStore(wafDataDir));
builder.Services.AddSingleton<WafEventStore>(_ => new WafEventStore(wafDataDir));
builder.Services.AddSingleton<WafEventIngestor>();

// 中控自身安全与审计（M3 安全班）：账号/会话/审计/SEC 回流。
builder.Services.AddSingleton<UserStore>(_ => new UserStore(wafDataDir));
builder.Services.AddSingleton<TokenStore>();
builder.Services.AddSingleton<AuditStore>(_ => new AuditStore(wafDataDir));
builder.Services.AddSingleton<AuthService>();
// FR-A16 两步验证：TOTP 落盘与校验服务。
builder.Services.AddSingleton<TotpStore>(_ => new TotpStore(wafDataDir));
builder.Services.AddSingleton<TotpService>();
builder.Services.AddSingleton<SecEventStore>(_ => new SecEventStore(wafDataDir));
builder.Services.AddSingleton<SecEventIngestor>();
builder.Services.AddSingleton<IntrusionDetector>();

// 统一告警中心（FR-A19）：落盘 dataDir/alerts/，入侵检测与心跳磁盘水位接线。
builder.Services.AddSingleton<AlertStore>(_ => new AlertStore(wafDataDir));
// 自定义告警推送（FR-A20）：渠道/白名单/日志落盘 dataDir/push/，critical 触发接线告警中心。
builder.Services.AddSingleton<Push.PushChannelStore>(_ => new Push.PushChannelStore(wafDataDir));
builder.Services.AddSingleton<Push.SsrfWhitelistStore>(_ => new Push.SsrfWhitelistStore(wafDataDir));
builder.Services.AddSingleton<Push.PushLogStore>(_ => new Push.PushLogStore(wafDataDir));
builder.Services.AddSingleton<Push.PushDispatcher>();
builder.Services.AddSingleton<AlertCenter>(sp =>
{
    var push = new Push.PushDispatcher(
        sp.GetRequiredService<Push.PushChannelStore>(),
        sp.GetRequiredService<Push.SsrfWhitelistStore>(),
        sp.GetRequiredService<Push.PushLogStore>());
    return new AlertCenter(sp.GetRequiredService<AlertStore>(), push);
});
// 节点间备份中转（FR-A13）：源端分片流转发，过账不落盘，流量统计面板可查。
builder.Services.AddSingleton<TaoOps.Server.Relay.RelayHub>();
// 事件回流自动订阅（M3 企业层班 A4）：节点上线后免手工触发即建立 WAF/SEC 订阅。
builder.Services.AddHostedService<StreamIngestorHostedService>();

// 传输模式（契约 §1）：TAOOPS_INSECURE=1 走明文 h2c（仅本地回环测试）；
// 默认 TLS，中控自签证书落数据目录，重启复用。
var insecure = Environment.GetEnvironmentVariable("TAOOPS_INSECURE") == "1";
var legacyUrls = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"));
Program.ConfigureTransport(builder, insecure, legacyUrls);

var app = builder.Build();
app.UseWebSockets();
// 中间件次序：审计在外（401/403 拒绝也留痕），鉴权在内；仅作用于 /api。
app.UseMiddleware<AuditMiddleware>();
app.UseMiddleware<AuthMiddleware>();
app.MapGrpcService<AgentService>();
app.MapGet("/", () => insecure ? "TaoOps Server M2 (insecure)" : "TaoOps Server M2");

// WebSocket 桥接（契约 §1）：终端 /ws/agent/{node}/terminal[?session=]、
// 日志 /ws/agent/{node}/logs?path=&tail=[&target=file|container]（FR-A11 容器日志回流）
app.Map("/ws/agent/{nodeId}/terminal", async (HttpContext ctx, NodeStreamHub streams) =>
{
    await WsBridge.MapTerminal(ctx, streams, ctx.GetRouteValue("nodeId")!.ToString()!,
        ctx.Request.Query["session"].Count > 0 ? ctx.Request.Query["session"][0] : null);
});
app.Map("/ws/agent/{nodeId}/logs", async (HttpContext ctx, NodeStreamHub streams) =>
{
    var path = ctx.Request.Query["path"].Count > 0 ? ctx.Request.Query["path"][0]! : "";
    var tail = int.TryParse(ctx.Request.Query["tail"], out var t) ? t : 0;
    var target = ctx.Request.Query["target"].Count > 0 ? ctx.Request.Query["target"][0]! : "file";
    await WsBridge.MapLogs(ctx, streams, ctx.GetRouteValue("nodeId")!.ToString()!, path, tail, target);
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
app.MapPatch("/api/waf/rules/{id}/enabled", (string id, ToggleEnabled req, WafRuleStore store) =>
    store.SetEnabled(id, req.Enabled) ? Results.Ok() : Results.NotFound());
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

// ---- 认证与账号管理（M3 安全班，PRD FR-C2 面板侧）----
app.MapPost("/api/auth/login", (LoginRequest req, HttpContext http, AuthService auth, TotpService totp) =>
{
    var ip = http.Connection.RemoteIpAddress?.ToString() ?? "";
    var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var result = auth.Login(req.Username ?? "", req.Password ?? "", ip, nowMs);
    if (result.Locked) return Results.Json(new { error = "account locked" }, statusCode: 423);
    if (!result.Ok) return Results.Json(new { error = "invalid credentials" }, statusCode: 401);
    // FR-A16：密码正确且已启用 TOTP → 不发 token，要求二次校验。
    if (totp.IsEnabled(req.Username ?? ""))
        return Results.Json(new { totp_required = true });
    return Results.Json(new { token = result.Session!.Token, role = result.Session.Role.ToString(),
        username = result.Session.Username, expires_at_ms = result.Session.ExpiresAtMs });
});
// FR-A16：登录二次校验（6 位 TOTP 或恢复码），成功发会话。
app.MapPost("/api/auth/totp/verify", (TotpVerifyRequest req, HttpContext http, TotpService totp) =>
{
    var ip = http.Connection.RemoteIpAddress?.ToString() ?? "";
    var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var result = totp.VerifyLogin(req.Username ?? "", req.Code, ip, nowMs);
    if (result.Locked) return Results.Json(new { error = "account locked" }, statusCode: 423);
    if (!result.Ok || result.Session is null)
        return Results.Json(new { error = "invalid totp code" }, statusCode: 401);
    return Results.Json(new { token = result.Session.Token, role = result.Session.Role.ToString(),
        username = result.Session.Username, expires_at_ms = result.Session.ExpiresAtMs });
});
// FR-A16：绑定准备（需登录 + 密码复验），返回密钥与 otpauth URI（前端渲染二维码）。
app.MapPost("/api/auth/totp/setup", (TotpSetupRequest req, HttpContext http, TotpService totp) =>
{
    var ip = http.Connection.RemoteIpAddress?.ToString() ?? "";
    var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    try
    {
        var (secret, uri) = totp.Setup(req.Username ?? "", req.Password ?? "", ip, nowMs);
        return Results.Json(new { secret, otpauth_uri = uri });
    }
    catch (UnauthorizedAccessException) { return Results.Json(new { error = "password re-verification failed" }, statusCode: 401); }
});
// FR-A16：确认绑定（需密码复验），返回 10 枚恢复码明文（仅此一次）。
app.MapPost("/api/auth/totp/enable", (TotpEnableRequest req, HttpContext http, TotpService totp) =>
{
    var ip = http.Connection.RemoteIpAddress?.ToString() ?? "";
    var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    try
    {
        var (codes, err) = totp.Enable(req.Username ?? "", req.Code ?? "", ip, nowMs);
        if (err != "") return Results.BadRequest(new { error = err });
        return Results.Json(new { recovery_codes = codes });
    }
    catch (UnauthorizedAccessException) { return Results.Json(new { error = "password re-verification failed" }, statusCode: 401); }
});
// FR-A16：重新生成恢复码（需登录 + 密码复验），旧码全部失效。
app.MapPost("/api/auth/totp/recovery/regenerate", (TotpEnableRequest req, HttpContext http, TotpService totp) =>
{
    var ip = http.Connection.RemoteIpAddress?.ToString() ?? "";
    var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var (codes, err) = totp.RegenerateRecovery(req.Username ?? "", req.Password ?? "", ip, nowMs);
    if (err != "") return Results.BadRequest(new { error = err });
    return Results.Json(new { recovery_codes = codes });
});
// FR-A16：查询是否已启用（登录页据此决定是否展示二次校验）。
app.MapGet("/api/auth/totp/status", (string? username, TotpService totp) =>
    Results.Json(new { enabled = totp.IsEnabled(username ?? "") }));
app.MapPost("/api/auth/logout", (HttpContext ctx, TokenStore tokens) =>
{
    var raw = ctx.Request.Headers.Authorization.ToString();
    var token = raw.StartsWith("Bearer ", StringComparison.Ordinal) ? raw["Bearer ".Length..].Trim() : raw;
    tokens.Revoke(token);
    return Results.Ok();
});
app.MapGet("/api/auth/me", (HttpContext ctx) =>
    Results.Json(new { user = ctx.Items["taoops.user"] ?? "", role = ctx.Items["taoops.role"] ?? "" }));

// 首个管理员引导：仅账号库为空时放行（无出厂默认口令）。
app.MapPost("/api/auth/bootstrap", (BootstrapRequest req, UserStore users) =>
{
    if (users.Load().Count > 0)
        return Results.Json(new { error = "bootstrap closed: users exist" }, statusCode: 403);
    if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
        return Results.BadRequest(new { error = "username and password required" });
    var account = users.Add(new UserAccount
    {
        Username = req.Username,
        PasswordHash = AuthService.HashPassword(req.Password),
        Role = UserRole.Admin,
        CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    });
    return Results.Json(new { username = account.Username, role = account.Role.ToString() });
});

app.MapGet("/api/auth/users", (UserStore users) =>
    Results.Json(users.Load().Select(u => new { u.Username, u.Role, u.CreatedAtMs })));
app.MapPost("/api/auth/users", (CreateUserRequest req, UserStore users) =>
{
    try
    {
        if (!RoleParser.TryParse(req.Role, out var role))
            return Results.BadRequest(new { error = "role must be Admin, Operator or ReadOnly" });
        var account = users.Add(new UserAccount
        {
            Username = req.Username ?? "",
            PasswordHash = AuthService.HashPassword(req.Password ?? ""),
            Role = role,
            CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        return Results.Json(new { username = account.Username, role = account.Role.ToString() });
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapDelete("/api/auth/users/{username}", (string username, UserStore users) =>
    users.Remove(username) ? Results.Ok() : Results.NotFound());
app.MapGet("/api/auth/lockouts", (AuthService auth) =>
    Results.Json(auth.Lockouts(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())));

// ---- 审计（FR-D4：留痕/哈希链/查询/导出）----
app.MapGet("/api/audit", (AuditStore store, string? user, long? from, long? to) =>
    Results.Json(store.Query(user, from, to)));
app.MapGet("/api/audit/verify", (AuditStore store) =>
{
    var (valid, brokenAt) = store.VerifyChain();
    return Results.Json(new { valid, broken_at_seq = brokenAt });
});
app.MapGet("/api/audit/export", (AuditStore store, string? format) =>
    format == "csv"
        ? Results.Text(store.ExportCsv(), "text/csv", System.Text.Encoding.UTF8)
        : Results.Text(store.ExportJson(), "application/json"));

// ---- SSH 防爆破事件查询（FR-C2，Agent secguard 经 LogStream 回流）----
app.MapGet("/api/sec/events", (SecEventStore store, string? nodeId, long? from, long? to) =>
    Results.Json(store.Query(nodeId, from, to)));

// ---- 企业层入侵检测告警查询（A11）：按类型/级别/时间过滤 ----
app.MapGet("/api/sec/alerts", (IntrusionDetector detector, string? type, string? severity, long? fromMs, long? toMs) =>
    Results.Json(detector.Query(type, severity, fromMs, toMs)));

// ---- 统一告警中心（FR-A19）：列表过滤/未读数/标记已读，保留默认 90 天 ----
app.MapGet("/api/alerts", (AlertStore store, string? level, string? source, string? nodeId,
        long? from, long? to, bool? unread) =>
    Results.Json(store.Query(level, source, nodeId, from, to, unread)));
app.MapGet("/api/alerts/unread", (AlertStore store) => Results.Json(new { count = store.UnreadCount() }));
app.MapPost("/api/alerts/{id}/read", (string id, AlertStore store) =>
    store.MarkRead(id) ? Results.Ok() : Results.NotFound());
app.MapPost("/api/alerts/read-all", (AlertStore store) => Results.Json(new { marked = store.MarkAllRead() }));

// ---- 节点间备份中转流量统计（FR-A13，契约 §7）----
app.MapGet("/api/relay/stats", (TaoOps.Server.Relay.RelayHub relay) => Results.Json(relay.Stats()));

// ---- 自定义告警推送（FR-A20，契约 §9）：渠道/白名单/测试/日志，全部 Admin ----
app.MapGet("/api/push/channels", (Push.PushChannelStore store) => Results.Json(store.List()));
app.MapPost("/api/push/channels", async (PushChannelReq req, HttpContext http,
    Push.PushChannelStore store, Push.SsrfWhitelistStore whitelist, AuditStore audit) =>
{
    var url = req.Url ?? "";
    var ssrfError = await Push.SsrfGuard.ValidateTargetAsync(url, whitelist.List());
    if (ssrfError is not null)
    {
        audit.Append(new AuditEvent { User = http.Items["taoops.user"]?.ToString() ?? "",
            Role = http.Items["taoops.role"]?.ToString() ?? "", Action = "push.channel",
            Target = url, Ip = http.Connection.RemoteIpAddress?.ToString() ?? "", Result = $"rejected: {ssrfError}" });
        return Results.Json(new { error = ssrfError }, statusCode: 400);
    }
    var channel = store.Upsert(new Push.PushChannel { Id = req.Id ?? "", Name = req.Name ?? "", Url = url, Enabled = req.Enabled ?? true });
    audit.Append(new AuditEvent { User = http.Items["taoops.user"]?.ToString() ?? "",
        Role = http.Items["taoops.role"]?.ToString() ?? "", Action = "push.channel",
        Target = url, Ip = http.Connection.RemoteIpAddress?.ToString() ?? "", Result = "ok" });
    return Results.Json(channel);
});
app.MapDelete("/api/push/channels/{id}", (string id, HttpContext http,
    Push.PushChannelStore store, AuditStore audit) =>
{
    var ok = store.Delete(id);
    audit.Append(new AuditEvent { User = http.Items["taoops.user"]?.ToString() ?? "",
        Role = http.Items["taoops.role"]?.ToString() ?? "", Action = "push.channel",
        Target = id, Ip = http.Connection.RemoteIpAddress?.ToString() ?? "", Result = ok ? "ok" : "not_found" });
    return ok ? Results.Ok() : Results.NotFound();
});
app.MapGet("/api/push/whitelist", (Push.SsrfWhitelistStore store) => Results.Json(store.List()));
app.MapPut("/api/push/whitelist", (WhitelistReq req, HttpContext http,
    Push.SsrfWhitelistStore store, AuditStore audit) =>
{
    var (oldEntries, newEntries) = store.Replace(req.Entries ?? []);
    // 变更进审计哈希链：逐条记录新增与移除。
    foreach (var added in newEntries.Where(e => !oldEntries.Contains(e, StringComparer.OrdinalIgnoreCase)))
        audit.Append(new AuditEvent { User = http.Items["taoops.user"]?.ToString() ?? "",
            Role = http.Items["taoops.role"]?.ToString() ?? "", Action = "push.whitelist",
            Target = added, Ip = http.Connection.RemoteIpAddress?.ToString() ?? "", Result = "added" });
    foreach (var removed in oldEntries.Where(e => !newEntries.Contains(e, StringComparer.OrdinalIgnoreCase)))
        audit.Append(new AuditEvent { User = http.Items["taoops.user"]?.ToString() ?? "",
            Role = http.Items["taoops.role"]?.ToString() ?? "", Action = "push.whitelist",
            Target = removed, Ip = http.Connection.RemoteIpAddress?.ToString() ?? "", Result = "removed" });
    return Results.Json(newEntries);
});
app.MapPost("/api/push/test", async (TestPushReq req, Push.PushChannelStore channels,
    Push.SsrfWhitelistStore whitelist, Push.PushDispatcher dispatcher) =>
{
    var channel = channels.List().FirstOrDefault(c => c.Id == req.ChannelId);
    if (channel is null) return Results.Json(new { error = "channel not found" }, statusCode: 404);
    var ssrfError = await Push.SsrfGuard.ValidateTargetAsync(channel.Url, whitelist.List());
    if (ssrfError is not null) return Results.Json(new { error = ssrfError }, statusCode: 400);
    var results = await dispatcher.SendAsync(new Alert
    {
        Id = "test", Source = "push-test", Level = AlertLevels.Critical,
        NodeId = "-", Detail = "手动测试推送", Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    });
    return Results.Json(results);
});
app.MapGet("/api/push/log", (Push.PushLogStore store, string? channelId, long? from, long? to) =>
    Results.Json(store.Query(channelId, from, to)));

// ---- 企业层 RBAC 权限清单（A13）：静态矩阵可查 ----
app.MapGet("/api/auth/permissions", (HttpContext ctx) =>
{
    var role = Enum.TryParse<UserRole>(ctx.Items["taoops.role"]?.ToString(), out var r) ? r : UserRole.ReadOnly;
    return Results.Json(new
    {
        role = role.ToString(),
        permissions = PermissionMatrix.For(role).Order().ToArray(),
        all_permissions = Permission.All,
    });
});

// ---- 节点列表与 HTTP 下发桥（项5，契约 §6）----
app.MapGet("/api/nodes", (NodeCommandBridge bridge) => Results.Json(bridge.ListNodes()));
// FR-A11 Docker 专用端点：按操作分级鉴权（DockerPolicy），经下发桥审计留痕。
app.MapPost("/api/nodes/{id}/docker", async (string id, DockerOpRequest req, HttpContext http, NodeCommandBridge bridge) =>
{
    var roleStr = http.Items["taoops.role"]?.ToString() ?? "";
    if (!Enum.TryParse<UserRole>(roleStr, out var role) || !DockerPolicy.Allows(req.Op ?? "", role))
    {
        return Results.Json(new { error = "insufficient role for docker op" }, statusCode: 403);
    }
    var args = DockerOpArgs.Build(req);
    try
    {
        var outcome = await bridge.DispatchAsync(id, "docker.manage", args, req.TimeoutMs,
            http.Items["taoops.user"]?.ToString() ?? "", roleStr,
            http.Connection.RemoteIpAddress?.ToString() ?? "");
        return Results.Json(new { exit_code = outcome.ExitCode, output = outcome.Output, request_id = outcome.RequestId });
    }
    catch (BridgeException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
});
// FR-A12 网站统计专用端点：按操作分级鉴权（WebStatsPolicy），经下发桥审计留痕。
app.MapPost("/api/nodes/{id}/webstats", async (string id, WebStatsOpRequest req, HttpContext http, NodeCommandBridge bridge) =>
{
    var roleStr = http.Items["taoops.role"]?.ToString() ?? "";
    if (!Enum.TryParse<UserRole>(roleStr, out var role) || !WebStatsPolicy.Allows(req.Op ?? "", role))
    {
        return Results.Json(new { error = "insufficient role for webstats op" }, statusCode: 403);
    }
    var args = WebStatsOpArgs.Build(req);
    try
    {
        var outcome = await bridge.DispatchAsync(id, "webstats.manage", args, req.TimeoutMs,
            http.Items["taoops.user"]?.ToString() ?? "", roleStr,
            http.Connection.RemoteIpAddress?.ToString() ?? "");
        return Results.Json(new { exit_code = outcome.ExitCode, output = outcome.Output, request_id = outcome.RequestId });
    }
    catch (BridgeException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
});
// FR-A14 一键迁移专用端点：按操作分级鉴权（MigratePolicy），经下发桥审计留痕。
app.MapPost("/api/nodes/{id}/migrate", async (string id, MigrateOpRequest req, HttpContext http, NodeCommandBridge bridge) =>
{
    var roleStr = http.Items["taoops.role"]?.ToString() ?? "";
    if (!Enum.TryParse<UserRole>(roleStr, out var role) || !MigratePolicy.Allows(req.Op ?? "", role))
    {
        return Results.Json(new { error = "insufficient role for migrate op" }, statusCode: 403);
    }
    var args = MigrateOpArgs.Build(req);
    try
    {
        var outcome = await bridge.DispatchAsync(id, "migrate.manage", args, req.TimeoutMs,
            http.Items["taoops.user"]?.ToString() ?? "", roleStr,
            http.Connection.RemoteIpAddress?.ToString() ?? "");
        return Results.Json(new { exit_code = outcome.ExitCode, output = outcome.Output, request_id = outcome.RequestId });
    }
    catch (BridgeException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
});
// FR-A18 负载均衡专用端点：按操作分级鉴权（LbPolicy），经下发桥审计留痕。
app.MapPost("/api/nodes/{id}/lb", async (string id, LbOpRequest req, HttpContext http, NodeCommandBridge bridge) =>
{
    var roleStr = http.Items["taoops.role"]?.ToString() ?? "";
    if (!Enum.TryParse<UserRole>(roleStr, out var role) || !LbPolicy.Allows(req.Op ?? "", role))
    {
        return Results.Json(new { error = "insufficient role for lb op" }, statusCode: 403);
    }
    var args = LbOpArgs.Build(req);
    try
    {
        var outcome = await bridge.DispatchAsync(id, "lb.manage", args, req.TimeoutMs,
            http.Items["taoops.user"]?.ToString() ?? "", roleStr,
            http.Connection.RemoteIpAddress?.ToString() ?? "");
        return Results.Json(new { exit_code = outcome.ExitCode, output = outcome.Output, request_id = outcome.RequestId });
    }
    catch (BridgeException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
});
app.MapPost("/api/nodes/{id}/cmd", async (string id, NodeCmdRequest req, HttpContext http, NodeCommandBridge bridge) =>
{
    try
    {
        var outcome = await bridge.DispatchAsync(id, req.Command, req.Args, req.TimeoutMs,
            http.Items["taoops.user"]?.ToString() ?? "", http.Items["taoops.role"]?.ToString() ?? "",
            http.Connection.RemoteIpAddress?.ToString() ?? "");
        return Results.Json(new { exit_code = outcome.ExitCode, output = outcome.Output, request_id = outcome.RequestId });
    }
    catch (BridgeException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
});

app.Run();

// ---- 传输装配（可测化：Kestrel TLS/h2c 配置抽出，集成测试直测三分支）----
public partial class Program
{
    /// <summary>
    /// 传输模式装配（契约 §1）：默认 TLS（自签证书落数据目录，重启复用）；
    /// TAOOPS_INSECURE=1 走明文 h2c（仅本地回环测试）；insecure + ASPNETCORE_URLS
    /// 沿用 --urls 明文端点（M1 E2E 兼容路径，不另配 Kestrel）。
    /// </summary>
    internal static void ConfigureTransport(WebApplicationBuilder builder, bool insecure, bool legacyUrls)
    {
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
    }
}

// ---- 请求模型（M3 安全班）----
public sealed record LoginRequest(string? Username, string? Password);
public sealed record ToggleEnabled(bool Enabled);
public sealed record BootstrapRequest(string? Username, string? Password);
public sealed record CreateUserRequest(string? Username, string? Password, string? Role);
public sealed record NodeCmdRequest(string? Command, IReadOnlyList<string>? Args, int TimeoutMs);

// ---- FR-A11 Docker 下发请求 ----
public sealed record DockerOpRequest(string? Op, string? Name, string? Ref, string? Command, int? Tail, int TimeoutMs);

// ---- FR-A12 网站统计下发请求 ----
public sealed record WebStatsOpRequest(string? Op, string? Site, string? Path, string? From, string? To, int TimeoutMs);

// ---- FR-A14 一键迁移下发请求 ----
public sealed record MigrateOpRequest(string? Op, string? SourcePath, string? TargetPath, string? Name,
    string? File, string? Mapping, string? SourceVersion, bool? Confirm, bool? Force, int TimeoutMs);

// ---- FR-A18 负载均衡下发请求 ----
public sealed record LbOpRequest(string? Op, string? Name, string? Host, int? Port, int? Weight, bool? Backup,
    string? HealthMode, string? HealthPath, int TimeoutMs);

// ---- FR-A16 两步验证请求 ----
public sealed record TotpVerifyRequest(string? Username, string? Code);
public sealed record TotpSetupRequest(string? Username, string? Password);
public sealed record TotpEnableRequest(string? Username, string? Code, string? Password);

// ---- FR-A20 告警推送请求 ----
public sealed record PushChannelReq(string? Id, string? Name, string? Url, bool? Enabled);
public sealed record WhitelistReq(IReadOnlyList<string>? Entries);
public sealed record TestPushReq(string? ChannelId);

internal static class MigrateOpArgs
{
    /// <summary>把请求字段摊平成 key=value 参数（Agent 侧 parseKeyValues 约定）。</summary>
    public static IReadOnlyList<string> Build(MigrateOpRequest req)
    {
        var args = new List<string> { $"op={req.Op}" };
        if (!string.IsNullOrEmpty(req.SourcePath)) args.Add($"source_path={req.SourcePath}");
        if (!string.IsNullOrEmpty(req.TargetPath)) args.Add($"target_path={req.TargetPath}");
        if (!string.IsNullOrEmpty(req.Name)) args.Add($"name={req.Name}");
        if (!string.IsNullOrEmpty(req.File)) args.Add($"file={req.File}");
        if (!string.IsNullOrEmpty(req.Mapping)) args.Add($"mapping={req.Mapping}");
        if (!string.IsNullOrEmpty(req.SourceVersion)) args.Add($"source_version={req.SourceVersion}");
        if (req.Confirm is bool c) args.Add($"confirm={c.ToString().ToLowerInvariant()}");
        if (req.Force is bool f) args.Add($"force={f.ToString().ToLowerInvariant()}");
        return args;
    }
}

internal static class LbOpArgs
{
    /// <summary>把请求字段摊平成 key=value 参数（Agent 侧 parseKeyValues 约定）。</summary>
    public static IReadOnlyList<string> Build(LbOpRequest req)
    {
        var args = new List<string> { $"op={req.Op}" };
        if (!string.IsNullOrEmpty(req.Name)) args.Add($"name={req.Name}");
        if (!string.IsNullOrEmpty(req.Host)) args.Add($"host={req.Host}");
        if (req.Port is int port) args.Add($"port={port}");
        if (req.Weight is int weight) args.Add($"weight={weight}");
        if (req.Backup is bool backup) args.Add($"backup={backup.ToString().ToLowerInvariant()}");
        if (!string.IsNullOrEmpty(req.HealthMode)) args.Add($"health_mode={req.HealthMode}");
        if (!string.IsNullOrEmpty(req.HealthPath)) args.Add($"health_path={req.HealthPath}");
        return args;
    }
}

internal static class WebStatsOpArgs
{
    /// <summary>把请求字段摊平成 key=value 参数（Agent 侧 parseKeyValues 约定）。</summary>
    public static IReadOnlyList<string> Build(WebStatsOpRequest req)
    {
        var args = new List<string> { $"op={req.Op}" };
        if (!string.IsNullOrEmpty(req.Site)) args.Add($"site={req.Site}");
        if (!string.IsNullOrEmpty(req.Path)) args.Add($"path={req.Path}");
        if (!string.IsNullOrEmpty(req.From)) args.Add($"from={req.From}");
        if (!string.IsNullOrEmpty(req.To)) args.Add($"to={req.To}");
        return args;
    }
}

internal static class DockerOpArgs
{
    /// <summary>把请求字段摊平成 key=value 参数（Agent 侧 parseKeyValues 约定）。</summary>
    public static IReadOnlyList<string> Build(DockerOpRequest req)
    {
        var args = new List<string> { $"op={req.Op}" };
        if (!string.IsNullOrEmpty(req.Name)) args.Add($"name={req.Name}");
        if (!string.IsNullOrEmpty(req.Ref)) args.Add($"ref={req.Ref}");
        if (!string.IsNullOrEmpty(req.Command)) args.Add($"command={req.Command}");
        if (req.Tail is int tail) args.Add($"tail={tail}");
        return args;
    }
}
