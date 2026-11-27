using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Text.Json;
using TaoOps.Server;
using TaoOps.Server.Alerts;
using TaoOps.Server.Audit;
using TaoOps.Server.Auth;
using Push = TaoOps.Server.Push;
using TaoOps.Server.Relay;
using TaoOps.Server.Sec;
using TaoOps.Server.Services;
using TaoOps.Server.Services.K8s;
using TaoOps.Server.Settings;
using TaoOps.Server.Waf;

var builder = WebApplication.CreateBuilder(args);
// Windows 版 FR-A31：以 Windows 服务方式常驻（Linux 沿用 systemd/裸跑，不受影响）。
builder.Services.AddWindowsService(o => o.ServiceName = "TaoOps");
builder.Services.AddGrpc();
builder.Services.AddSingleton<NodeRegistry>();
builder.Services.AddSingleton<NodeCommandHub>();
builder.Services.AddSingleton<NodeStreamHub>();
builder.Services.AddSingleton<NodeCommandBridge>();
// FR-A30 多机批量下发（PRD v1.4）。
builder.Services.AddSingleton<BatchService>();

// WAF 管理面（M3）：规则真源与事件账本落数据目录（FR-A31：平台化解析，Windows 走 %ProgramData%\TaoOps\data）。
var wafDataDir = TaoOpsPaths.DataDir;
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
// 系统设置存储（backlog F4）：告警阈值等落盘 dataDir/settings.json。
builder.Services.AddSingleton<SettingsStore>(_ => new SettingsStore(wafDataDir));
builder.Services.AddSingleton<AlertCenter>(sp =>
{
    var push = new Push.PushDispatcher(
        sp.GetRequiredService<Push.PushChannelStore>(),
        sp.GetRequiredService<Push.SsrfWhitelistStore>(),
        sp.GetRequiredService<Push.PushLogStore>());
    return new AlertCenter(sp.GetRequiredService<AlertStore>(), push,
        sp.GetRequiredService<SettingsStore>());
});
// FR-A15 复制中断巡检：60s 轮询在线节点 replica_status，中断/超阈值接告警中心。
builder.Services.AddSingleton<ReplicationMonitor>(sp => new ReplicationMonitor(
    sp.GetRequiredService<NodeCommandBridge>(),
    () => sp.GetRequiredService<NodeCommandBridge>().ListNodes().Select(n => (n.NodeId, n.CommandConnected)),
    sp.GetRequiredService<AlertCenter>(),
    sp.GetRequiredService<ILogger<ReplicationMonitor>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<ReplicationMonitor>());
// FR-A21 证书到期巡检：24h 轮询在线节点 ssl 清单，到期前 30 天尝试续期，失败接告警中心。
builder.Services.AddSingleton<SslMonitor>(sp => new SslMonitor(
    sp.GetRequiredService<NodeCommandBridge>(),
    () => sp.GetRequiredService<NodeCommandBridge>().ListNodes().Select(n => (n.NodeId, n.CommandConnected)),
    sp.GetRequiredService<AlertCenter>(),
    sp.GetRequiredService<ILogger<SslMonitor>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<SslMonitor>());
// FR-A22 节点离线巡检：60s 检查命令通道状态，断连超阈值边缘触发 warn 告警，恢复复位。
builder.Services.AddSingleton<NodeOfflineMonitor>(sp => new NodeOfflineMonitor(
    () => sp.GetRequiredService<NodeCommandBridge>().ListNodes().Select(n => (n.NodeId, n.CommandConnected)),
    sp.GetRequiredService<AlertCenter>(),
    sp.GetRequiredService<ILogger<NodeOfflineMonitor>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<NodeOfflineMonitor>());
// FR-A26 备份策略调度：60s 轮询在线节点策略清单，到期即触发执行，失败边缘触发 warn 告警。
builder.Services.AddSingleton<BackupScheduleMonitor>(sp => new BackupScheduleMonitor(
    sp.GetRequiredService<NodeCommandBridge>(),
    () => sp.GetRequiredService<NodeCommandBridge>().ListNodes().Select(n => (n.NodeId, n.CommandConnected)),
    sp.GetRequiredService<AlertCenter>(),
    sp.GetRequiredService<ILogger<BackupScheduleMonitor>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<BackupScheduleMonitor>());
// FR-A23 资源实时监控：60s 对在线节点采样 sysinfo.overview op=metrics_sample，
// 入内存环形缓冲（24h×60s=1440 点，不落盘重启清零），采样失败静默。
builder.Services.AddSingleton<MetricsStore>();
// FR-A29 AI 运维建议（PRD v1.4）：只读门面 + 规则引擎 + 外接模式。
builder.Services.AddSingleton<AiAdviceExternal>(_ => new AiAdviceExternal(wafDataDir,
    new AuditStore(wafDataDir)));
builder.Services.AddSingleton<AdviceEngine>();
builder.Services.AddSingleton<IAdviceDataSource>(sp => new DefaultAdviceDataSource(
    sp.GetRequiredService<MetricsStore>(),
    sp.GetRequiredService<AlertStore>(),
    sp.GetRequiredService<NodeCommandBridge>()));
builder.Services.AddSingleton<MetricsSampler>(sp => new MetricsSampler(
    sp.GetRequiredService<NodeCommandBridge>(),
    () => sp.GetRequiredService<NodeCommandBridge>().ListNodes().Select(n => (n.NodeId, n.CommandConnected)),
    sp.GetRequiredService<MetricsStore>(),
    sp.GetRequiredService<ILogger<MetricsSampler>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<MetricsSampler>());
// FR-A28 K8s 集成：kubeconfig 只存中控（落盘 600），中控直连集群，Agent 不参与。
builder.Services.AddSingleton<KubeClusterStore>(_ => new KubeClusterStore(wafDataDir));
builder.Services.AddSingleton<IK8sClientFactory, K8sClientFactory>();
builder.Services.AddSingleton<K8sClusterService>();
builder.Services.AddSingleton<K8sMonitor>(sp => new K8sMonitor(
    sp.GetRequiredService<KubeClusterStore>(),
    sp.GetRequiredService<IK8sClientFactory>(),
    sp.GetRequiredService<AlertCenter>(),
    sp.GetRequiredService<ILogger<K8sMonitor>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<K8sMonitor>());
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
// FR-A31 界面自带：web/dist 随中控分发（安装器拷到程序目录 web/，或 TAOOPS_WEB_DIST 指定）。
// 命中时静态文件优先于 MapGet("/")，SPA 路由回退 index.html；未命中保持 API-only 既有行为。
var webDist = TaoOpsPaths.ResolveWebDist(
    Environment.GetEnvironmentVariable("TAOOPS_WEB_DIST"), AppContext.BaseDirectory);
if (webDist is not null)
{
    var webProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webDist);
    // 注意：DefaultFilesOptions 无参构造不填 DefaultFileNames（internal 构造才填），必须显式设置。
    var defaultFiles = new DefaultFilesOptions { FileProvider = webProvider };
    defaultFiles.DefaultFileNames = ["index.html"];
    app.UseDefaultFiles(defaultFiles);
    app.UseStaticFiles(new StaticFileOptions { FileProvider = webProvider });
    app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = webProvider });
}
app.UseWebSockets();
// 中间件次序：审计在外（401/403 拒绝也留痕），鉴权在内；仅作用于 /api。
app.UseMiddleware<AuditMiddleware>();
app.UseMiddleware<AuthMiddleware>();
// 显式路由匹配：必须置于静态文件之后。WebApplication 缺省把 UseRouting 自动插到管道最前，
// 而 StaticFilesMiddleware 遇到「已匹配端点」会主动让路（ValidateNoEndpoint），
// 导致自带界面永远落到 MapGet("/")。显式调用后静态文件先于端点匹配短路。
app.UseRouting();
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
    var ip = http.ClientIp();
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
    var ip = http.ClientIp();
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
    var ip = http.ClientIp();
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
    var ip = http.ClientIp();
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
    var ip = http.ClientIp();
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

// 修改自己密码（backlog F2）：旧密码校验 + 新密码落哈希；审计留痕不含密码内容。
app.MapPost("/api/auth/change-password", (ChangePasswordRequest req, HttpContext http,
    UserStore users, AuditStore audit) =>
{
    var username = http.AuthUser();
    var account = users.Find(username);
    if (account is null) return Results.Json(new { error = "user not found" }, statusCode: 404);
    if (!AuthService.VerifyPassword(req.OldPassword ?? "", account.PasswordHash))
    {
        audit.Append(new AuditEvent { User = username, Role = http.AuthRole(),
            Action = "auth.change_password", Target = username,
            Ip = http.ClientIp(), Result = "old_password_mismatch" });
        return Results.Json(new { error = "旧密码不正确" }, statusCode: 400);
    }
    if (string.IsNullOrEmpty(req.NewPassword) || req.NewPassword.Length < 8)
        return Results.Json(new { error = "新密码至少 8 位" }, statusCode: 400);
    users.UpdatePasswordHash(username, AuthService.HashPassword(req.NewPassword));
    audit.Append(new AuditEvent { User = username, Role = http.AuthRole(),
        Action = "auth.change_password", Target = username,
        Ip = http.ClientIp(), Result = "ok" });
    return Results.Ok();
});

// 系统设置（backlog F4）：告警阈值可视化。GET 回显（登录即可），PUT 落盘（Admin only）。
app.MapGet("/api/settings/alerts", (AlertCenter alerts) =>
    Results.Json(new { disk_critical_pct = alerts.DiskCriticalPct, disk_reset_pct = alerts.DiskResetPct }));
app.MapPut("/api/settings/alerts", (AlertThresholdSettings req, HttpContext http,
    SettingsStore store, AlertCenter alerts, AuditStore audit) =>
{
    var roleStr = http.AuthRole();
    if (!Enum.TryParse<UserRole>(roleStr, out var role) || role != UserRole.Admin)
        return Results.Json(new { error = "admin role required" }, statusCode: 403);
    if (req.DiskCriticalPct is < 1 or > 99) return Results.Json(new { error = "disk_critical_pct 须在 1-99" }, statusCode: 400);
    if (req.DiskResetPct is < 1 or > 99) return Results.Json(new { error = "disk_reset_pct 须在 1-99" }, statusCode: 400);
    if (req.DiskResetPct >= req.DiskCriticalPct)
        return Results.Json(new { error = "复位阈值须低于触发阈值（迟滞口径）" }, statusCode: 400);
    store.Save(req);
    alerts.ReloadThresholds(store);
    audit.Append(new AuditEvent { User = http.AuthUser(), Role = roleStr,
        Action = "settings.alerts", Target = "alert_thresholds",
        Ip = http.ClientIp(),
        Result = $"critical={req.DiskCriticalPct} reset={req.DiskResetPct}" });
    return Results.Json(new { disk_critical_pct = alerts.DiskCriticalPct, disk_reset_pct = alerts.DiskResetPct,
        note = "阈值已落盘并即时生效" });
});

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
        audit.Append(new AuditEvent { User = http.AuthUser(),
            Role = http.AuthRole(), Action = "push.channel",
            Target = url, Ip = http.ClientIp(), Result = $"rejected: {ssrfError}" });
        return Results.Json(new { error = ssrfError }, statusCode: 400);
    }
    var channel = store.Upsert(new Push.PushChannel { Id = req.Id ?? "", Name = req.Name ?? "", Url = url, Enabled = req.Enabled ?? true });
    audit.Append(new AuditEvent { User = http.AuthUser(),
        Role = http.AuthRole(), Action = "push.channel",
        Target = url, Ip = http.ClientIp(), Result = "ok" });
    return Results.Json(channel);
});
app.MapDelete("/api/push/channels/{id}", (string id, HttpContext http,
    Push.PushChannelStore store, AuditStore audit) =>
{
    var ok = store.Delete(id);
    audit.Append(new AuditEvent { User = http.AuthUser(),
        Role = http.AuthRole(), Action = "push.channel",
        Target = id, Ip = http.ClientIp(), Result = ok ? "ok" : "not_found" });
    return ok ? Results.Ok() : Results.NotFound();
});
app.MapGet("/api/push/whitelist", (Push.SsrfWhitelistStore store) => Results.Json(store.List()));
app.MapPut("/api/push/whitelist", (WhitelistReq req, HttpContext http,
    Push.SsrfWhitelistStore store, AuditStore audit) =>
{
    var (oldEntries, newEntries) = store.Replace(req.Entries ?? []);
    // 变更进审计哈希链：逐条记录新增与移除。
    foreach (var added in newEntries.Where(e => !oldEntries.Contains(e, StringComparer.OrdinalIgnoreCase)))
        audit.Append(new AuditEvent { User = http.AuthUser(),
            Role = http.AuthRole(), Action = "push.whitelist",
            Target = added, Ip = http.ClientIp(), Result = "added" });
    foreach (var removed in oldEntries.Where(e => !newEntries.Contains(e, StringComparer.OrdinalIgnoreCase)))
        audit.Append(new AuditEvent { User = http.AuthUser(),
            Role = http.AuthRole(), Action = "push.whitelist",
            Target = removed, Ip = http.ClientIp(), Result = "removed" });
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
// FR-A23 资源实时监控查询：内存环形缓冲，hours=1|24（默认 1），任意已登录角色可查。
app.MapGet("/api/nodes/{id}/metrics", (string id, MetricsStore store, int? hours) =>
{
    var h = hours is 1 or 24 ? hours.Value : 1;
    var since = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - h * 3_600_000L;
    return Results.Json(store.Query(id, since));
});
// FR-A29 AI 运维建议（PRD v1.4）：本地规则引擎按节点出建议；查看 ReadOnly 及以上。
app.MapGet("/api/advice/{id}", async (string id, HttpContext http, AdviceEngine engine,
    IAdviceDataSource source, SettingsStore settings) =>
{
    var roleStr = http.AuthRole();
    if (!Enum.TryParse<UserRole>(roleStr, out var role) || !role.AtLeast(UserRole.ReadOnly))
        return Results.Json(new { error = "insufficient role" }, statusCode: 403);
    var advices = await engine.AdviseAsync(id, settings.Load());
    return Results.Json(new
    {
        node_id = id,
        advices = advices.Select(a => new
        {
            id = a.Id, level = a.Level, title = a.Title,
            basis = a.Basis, action_link = a.ActionLink, action_text = a.ActionText,
        }),
    });
});

// FR-A29 外接模式配置（Admin）：Key 不回显，只返回 has_key 状态。
app.MapGet("/api/advice/external/config", (HttpContext http, AiAdviceExternal ext) =>
{
    var cfgRole = http.AuthRole();
    if (!Enum.TryParse<UserRole>(cfgRole, out var cfgParsed) || cfgParsed != UserRole.Admin)
        return Results.Json(new { error = "admin only" }, statusCode: 403);
    var (endpoint, hasKey, whitelist) = ext.LoadConfigSafe();
    return Results.Json(new { endpoint, has_key = hasKey, ssrf_whitelist = whitelist });
});
app.MapPost("/api/advice/external/config", (ExternalConfigRequest req, HttpContext http, AiAdviceExternal ext) =>
{
    var saveRole = http.AuthRole();
    if (!Enum.TryParse<UserRole>(saveRole, out var saveParsed) || saveParsed != UserRole.Admin)
        return Results.Json(new { error = "admin only" }, statusCode: 403);
    try
    {
        ext.SaveConfig(new AiAdviceExternal.ExternalConfig(
            req.Endpoint ?? "", req.ApiKey ?? "", req.SsrfWhitelist ?? []));
        return Results.Json(new { ok = true });
    }
    catch (ArgumentException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 400);
    }
});

// FR-A29 深度分析（Operator+）：脱敏摘要发用户自备端点，返回纯文本。
app.MapPost("/api/advice/{id}/analyze", async (string id, HttpContext http, AiAdviceExternal ext,
    IAdviceDataSource source, AdviceEngine engine, SettingsStore settings) =>
{
    var roleStr = http.AuthRole();
    if (!Enum.TryParse<UserRole>(roleStr, out var role) || !role.AtLeast(UserRole.Operator))
        return Results.Json(new { error = "insufficient role" }, statusCode: 403);
    var user = http.AuthUser();
    var ip = http.ClientIp();

    // 脱敏摘要：仅指标数值与规则标识（安全位评审口径——不含主机名/IP/节点名）。
    var advices = await engine.AdviseAsync(id, settings.Load());
    var summary = $"node metrics summary (anonymized):\n" +
                  string.Join("\n", advices.Select(a => $"[{a.Level}] {a.Title}: {a.Basis}"));
    if (advices.Count == 0) summary += "no local rule triggered; metrics within thresholds";

    try
    {
        var text = await ext.AnalyzeAsync(id, summary, user, roleStr, ip);
        return Results.Json(new { advice = text });
    }
    catch (AiAdviceExternal.AiExternalException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode);
    }
});

// FR-A30 多机批量下发（PRD v1.4）：白名单逐 op 枚举纯只读；入口 Operator 及以上；
// 服务端二次校验节点在线；限频/并发/截断在 BatchService 内。审计主+子记录进哈希链。
app.MapPost("/api/batch", async (BatchOpRequest req, HttpContext http, BatchService batch) =>
{
    var roleStr = http.AuthRole();
    if (!Enum.TryParse<UserRole>(roleStr, out var role) || !role.AtLeast(UserRole.Operator))
    {
        return Results.Json(new { error = "insufficient role for batch" }, statusCode: 403);
    }
    var args = (req.Args ?? []).Where(a => a is not null).Select(a => a!).ToList();
    try
    {
        var result = await batch.RunAsync(req.Module ?? "", args, req.NodeIds ?? [],
            http.AuthUser(), roleStr,
            http.ClientIp());
        return Results.Json(new
        {
            batch_id = result.BatchId,
            module = result.Module,
            success_count = result.SuccessCount,
            failure_count = result.FailureCount,
            nodes = result.Nodes.Select(n => new
            {
                node_id = n.NodeId, status = n.Status,
                exit_code = n.ExitCode, output = n.Output, request_id = n.RequestId,
            }),
        });
    }
    catch (BatchService.BatchException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode);
    }
});

// FR-A11 Docker 专用端点：按操作分级鉴权（DockerPolicy），经下发桥审计留痕。
app.MapPost("/api/nodes/{id}/docker", async (string id, DockerOpRequest req, HttpContext http, NodeCommandBridge bridge) =>
{
    var roleStr = http.AuthRole();
    if (!Enum.TryParse<UserRole>(roleStr, out var role) || !DockerPolicy.Allows(req.Op ?? "", role))
    {
        return Results.Json(new { error = "insufficient role for docker op" }, statusCode: 403);
    }
    var args = DockerOpArgs.Build(req);
    try
    {
        var outcome = await bridge.DispatchAsync(id, "docker.manage", args, req.TimeoutMs,
            http.AuthUser(), roleStr,
            http.ClientIp());
        return Results.Json(new { exit_code = outcome.ExitCode, output = outcome.Output, request_id = outcome.RequestId });
    }
    catch (BridgeException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
});
// FR-A12 网站统计专用端点：按操作分级鉴权（WebStatsPolicy），经下发桥审计留痕。
app.MapPost("/api/nodes/{id}/webstats", async (string id, WebStatsOpRequest req, HttpContext http, NodeCommandBridge bridge) =>
{
    var roleStr = http.AuthRole();
    if (!Enum.TryParse<UserRole>(roleStr, out var role) || !WebStatsPolicy.Allows(req.Op ?? "", role))
    {
        return Results.Json(new { error = "insufficient role for webstats op" }, statusCode: 403);
    }
    var args = WebStatsOpArgs.Build(req);
    try
    {
        var outcome = await bridge.DispatchAsync(id, "webstats.manage", args, req.TimeoutMs,
            http.AuthUser(), roleStr,
            http.ClientIp());
        return Results.Json(new { exit_code = outcome.ExitCode, output = outcome.Output, request_id = outcome.RequestId });
    }
    catch (BridgeException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
});
// 主机防火墙端点（backlog F3）：按操作分级鉴权（FwPolicy），经下发桥审计留痕。
app.MapPost("/api/nodes/{id}/fw", async (string id, FwOpRequest req, HttpContext http, NodeCommandBridge bridge) =>
{
    var roleStr = http.AuthRole();
    if (!Enum.TryParse<UserRole>(roleStr, out var role) || !FwPolicy.Allows(req.Op ?? "", role))
    {
        return Results.Json(new { error = "insufficient role for fw op" }, statusCode: 403);
    }
    var args = FwOpArgs.Build(req);
    try
    {
        var outcome = await bridge.DispatchAsync(id, "fw.manage", args, req.TimeoutMs,
            http.AuthUser(), roleStr,
            http.ClientIp());
        return Results.Json(new { exit_code = outcome.ExitCode, output = outcome.Output, request_id = outcome.RequestId });
    }
    catch (BridgeException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
});
// 防篡改状态端点（backlog F3）：status 只读，经下发桥审计留痕。
app.MapPost("/api/nodes/{id}/tamperguard", async (string id, TamperGuardOpRequest req, HttpContext http, NodeCommandBridge bridge) =>
{
    var roleStr = http.AuthRole();
    if (!Enum.TryParse<UserRole>(roleStr, out var role) || !TamperGuardPolicy.Allows(req.Op ?? "", role))
    {
        return Results.Json(new { error = "insufficient role for tamperguard op" }, statusCode: 403);
    }
    var args = new List<string> { $"op={req.Op}" };
    try
    {
        var outcome = await bridge.DispatchAsync(id, "tamper.guard", args, req.TimeoutMs,
            http.AuthUser(), roleStr,
            http.ClientIp());
        return Results.Json(new { exit_code = outcome.ExitCode, output = outcome.Output, request_id = outcome.RequestId });
    }
    catch (BridgeException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
});
// 计划任务专用端点（backlog B2 切真）：按操作分级鉴权（CronPolicy），经下发桥审计留痕。
app.MapPost("/api/nodes/{id}/cron", async (string id, CronOpRequest req, HttpContext http, NodeCommandBridge bridge) =>
{
    var roleStr = http.AuthRole();
    if (!Enum.TryParse<UserRole>(roleStr, out var role) || !CronPolicy.Allows(req.Op ?? "", role))
    {
        return Results.Json(new { error = "insufficient role for cron op" }, statusCode: 403);
    }
    var args = CronOpArgs.Build(req);
    try
    {
        var outcome = await bridge.DispatchAsync(id, "cron.manage", args, req.TimeoutMs,
            http.AuthUser(), roleStr,
            http.ClientIp());
        return Results.Json(new { exit_code = outcome.ExitCode, output = outcome.Output, request_id = outcome.RequestId });
    }
    catch (BridgeException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
});
// 网站专用端点（backlog B2 切真）：按操作分级鉴权（SitePolicy），经下发桥审计留痕。
app.MapPost("/api/nodes/{id}/site", async (string id, SiteOpRequest req, HttpContext http, NodeCommandBridge bridge) =>
{
    var roleStr = http.AuthRole();
    if (!Enum.TryParse<UserRole>(roleStr, out var role) || !SitePolicy.Allows(req.Op ?? "", role))
    {
        return Results.Json(new { error = "insufficient role for site op" }, statusCode: 403);
    }
    var args = SiteOpArgs.Build(req);
    try
    {
        var outcome = await bridge.DispatchAsync(id, "site.manage", args, req.TimeoutMs,
            http.AuthUser(), roleStr,
            http.ClientIp());
        return Results.Json(new { exit_code = outcome.ExitCode, output = outcome.Output, request_id = outcome.RequestId });
    }
    catch (BridgeException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
});
// FR-A14 一键迁移专用端点：按操作分级鉴权（MigratePolicy），经下发桥审计留痕。
app.MapPost("/api/nodes/{id}/migrate", async (string id, MigrateOpRequest req, HttpContext http, NodeCommandBridge bridge) =>
{
    var roleStr = http.AuthRole();
    if (!Enum.TryParse<UserRole>(roleStr, out var role) || !MigratePolicy.Allows(req.Op ?? "", role))
    {
        return Results.Json(new { error = "insufficient role for migrate op" }, statusCode: 403);
    }
    var args = MigrateOpArgs.Build(req);
    try
    {
        var outcome = await bridge.DispatchAsync(id, "migrate.manage", args, req.TimeoutMs,
            http.AuthUser(), roleStr,
            http.ClientIp());
        return Results.Json(new { exit_code = outcome.ExitCode, output = outcome.Output, request_id = outcome.RequestId });
    }
    catch (BridgeException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
});
// FR-A24 应用一键部署专用端点：按操作分级鉴权（AppDeployPolicy），经下发桥审计留痕。
app.MapPost("/api/nodes/{id}/apps", async (string id, AppDeployRequest req, HttpContext http, NodeCommandBridge bridge) =>
{
    var roleStr = http.AuthRole();
    if (!Enum.TryParse<UserRole>(roleStr, out var role) || !AppDeployPolicy.Allows(req.Op ?? "", role))
    {
        return Results.Json(new { error = "insufficient role for app deploy op" }, statusCode: 403);
    }
    var args = AppDeployArgs.Build(req);
    try
    {
        var outcome = await bridge.DispatchAsync(id, "app.deploy", args, req.TimeoutMs,
            http.AuthUser(), roleStr,
            http.ClientIp());
        return Results.Json(new { exit_code = outcome.ExitCode, output = outcome.Output, request_id = outcome.RequestId });
    }
    catch (BridgeException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
});

// ---- FR-A28 K8s 集成（契约 §15）：中控直连集群，Agent 不参与。 ----
// 集群管理（增删）=Admin；查看=ReadOnly；资源操作=Admin（K8sPolicy）。
// 审计脱敏：kubeconfig 原文与凭据内容不进审计、不进日志（安全红线）。
app.MapGet("/api/k8s/clusters", (KubeClusterStore store) => Results.Json(store.List()));
app.MapPost("/api/k8s/clusters", (K8sRequest req, HttpContext http,
    KubeClusterStore store, K8sClusterService svc, AuditStore audit) =>
{
    var roleStr = http.AuthRole();
    if (!Enum.TryParse<UserRole>(roleStr, out var role) || !K8sPolicy.Allows("add", role))
        return Results.Json(new { error = "insufficient role for k8s op" }, statusCode: 403);
    var name = req.Name ?? "";
    var kubeconfig = req.Kubeconfig ?? "";
    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(kubeconfig))
        return Results.Json(new { error = "name and kubeconfig required" }, statusCode: 400);
    string version;
    try { version = svc.TestVersion(kubeconfig); }
    catch (Exception ex)
    {
        // 连接测试失败：报错不含 kubeconfig 内容。
        audit.Append(new AuditEvent { User = http.AuthUser(),
            Role = roleStr, Action = "k8s.cluster", Target = name,
            Ip = http.ClientIp(), Result = "connect_failed" });
        return Results.Json(new { error = $"连接测试失败: {ex.Message}" }, statusCode: 400);
    }
    var meta = store.Save(name, kubeconfig, version);
    audit.Append(new AuditEvent { User = http.AuthUser(),
        Role = roleStr, Action = "k8s.cluster", Target = name,
        Ip = http.ClientIp(), Result = $"ok version={version}" });
    return Results.Json(meta);
});
app.MapDelete("/api/k8s/clusters/{name}", (string name, HttpContext http,
    KubeClusterStore store, AuditStore audit, bool confirm) =>
{
    var roleStr = http.AuthRole();
    if (!Enum.TryParse<UserRole>(roleStr, out var role) || !K8sPolicy.Allows("remove_cluster", role))
        return Results.Json(new { error = "insufficient role for k8s op" }, statusCode: 403);
    var ok = store.Remove(name, confirm);
    audit.Append(new AuditEvent { User = http.AuthUser(),
        Role = roleStr, Action = "k8s.cluster", Target = name,
        Ip = http.ClientIp(), Result = ok ? "removed" : (confirm ? "not_found" : "confirm_required") });
    return ok ? Results.Ok() : Results.Json(new { error = confirm ? "cluster not found" : "confirm=true required" },
        statusCode: confirm ? 404 : 400);
});
app.MapGet("/api/k8s/{cluster}/nodes", (string cluster, HttpContext http, K8sClusterService svc) =>
{
    var roleStr = http.AuthRole();
    if (!Enum.TryParse<UserRole>(roleStr, out var role) || !K8sPolicy.Allows("nodes", role))
        return Results.Json(new { error = "insufficient role for k8s op" }, statusCode: 403);
    try { return Results.Json(svc.ListNodes(cluster)); }
    catch (KeyNotFoundException) { return Results.Json(new { error = "cluster not found" }, statusCode: 404); }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 502); }
});
app.MapGet("/api/k8s/{cluster}/workloads", (string cluster, HttpContext http, K8sClusterService svc, string? ns) =>
{
    var roleStr = http.AuthRole();
    if (!Enum.TryParse<UserRole>(roleStr, out var role) || !K8sPolicy.Allows("workloads", role))
        return Results.Json(new { error = "insufficient role for k8s op" }, statusCode: 403);
    try { return Results.Json(svc.ListWorkloads(cluster, ns ?? "")); }
    catch (KeyNotFoundException) { return Results.Json(new { error = "cluster not found" }, statusCode: 404); }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 502); }
});
app.MapGet("/api/k8s/{cluster}/pods", (string cluster, HttpContext http, K8sClusterService svc, string? ns) =>
{
    var roleStr = http.AuthRole();
    if (!Enum.TryParse<UserRole>(roleStr, out var role) || !K8sPolicy.Allows("pods", role))
        return Results.Json(new { error = "insufficient role for k8s op" }, statusCode: 403);
    try { return Results.Json(svc.ListPods(cluster, ns ?? "")); }
    catch (KeyNotFoundException) { return Results.Json(new { error = "cluster not found" }, statusCode: 404); }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 502); }
});
// 集群健康总览（backlog C2）：全部集群节点/Pod 汇总，单集群巡检失败降级为 Error。
app.MapGet("/api/k8s/health", (HttpContext http, K8sClusterService svc) =>
{
    var roleStr = http.AuthRole();
    if (!Enum.TryParse<UserRole>(roleStr, out var role) || !K8sPolicy.Allows("nodes", role))
        return Results.Json(new { error = "insufficient role for k8s op" }, statusCode: 403);
    return Results.Json(svc.Health());
});
// Pod 资源用量（backlog C1）：metrics.k8s.io 自定义 API，未装 metrics-server 时降级说明。
app.MapGet("/api/k8s/{cluster}/metrics", async (string cluster, HttpContext http, K8sClusterService svc) =>
{
    var roleStr = http.AuthRole();
    if (!Enum.TryParse<UserRole>(roleStr, out var role) || !K8sPolicy.Allows("metrics", role))
        return Results.Json(new { error = "insufficient role for k8s op" }, statusCode: 403);
    try { return Results.Json(await svc.PodMetricsAsync(cluster)); }
    catch (KeyNotFoundException) { return Results.Json(new { error = "cluster not found" }, statusCode: 404); }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 502); }
});
app.MapGet("/api/k8s/{cluster}/events", (string cluster, HttpContext http, K8sClusterService svc, string? ns) =>
{
    var roleStr = http.AuthRole();
    if (!Enum.TryParse<UserRole>(roleStr, out var role) || !K8sPolicy.Allows("events", role))
        return Results.Json(new { error = "insufficient role for k8s op" }, statusCode: 403);
    try { return Results.Json(svc.ListEvents(cluster, ns ?? "")); }
    catch (KeyNotFoundException) { return Results.Json(new { error = "cluster not found" }, statusCode: 404); }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 502); }
});
// Pod 日志：读操作但进审计留痕（安全评审保留意见③）。
app.MapGet("/api/k8s/{cluster}/logs", (string cluster, HttpContext http, K8sClusterService svc,
    AuditStore audit, string ns, string pod, string? container) =>
{
    var roleStr = http.AuthRole();
    if (!Enum.TryParse<UserRole>(roleStr, out var role) || !K8sPolicy.Allows("logs", role))
        return Results.Json(new { error = "insufficient role for k8s op" }, statusCode: 403);
    try
    {
        var log = svc.GetPodLog(cluster, ns, pod, container);
        audit.Append(new AuditEvent { User = http.AuthUser(),
            Role = roleStr, Action = "k8s.logs", Target = $"{cluster}/{ns}/{pod}",
            Ip = http.ClientIp(), Result = "ok" });
        return Results.Content(log);
    }
    catch (KeyNotFoundException) { return Results.Json(new { error = "cluster not found" }, statusCode: 404); }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 502); }
});
// 资源操作：scale/restart/rollout_status/deploy/remove，变更限 Admin，审计留痕。
app.MapPost("/api/k8s/{cluster}/ops", (string cluster, K8sRequest req, HttpContext http,
    K8sClusterService svc, AuditStore audit) =>
{
    var roleStr = http.AuthRole();
    if (!Enum.TryParse<UserRole>(roleStr, out var role) || !K8sPolicy.Allows(req.Op ?? "", role))
    {
        audit.Append(new AuditEvent { User = http.AuthUser(),
            Role = roleStr, Action = "k8s.op", Target = $"{cluster}/{req.Op}",
            Ip = http.ClientIp(), Result = "denied" });
        return Results.Json(new { error = "insufficient role for k8s op" }, statusCode: 403);
    }
    try
    {
        object result = (req.Op ?? "") switch
        {
            "scale" => svc.Scale(cluster, req.Namespace ?? "default", req.Deployment ?? "",
                int.TryParse(req.Replicas, out var r) ? r : -1),
            "restart" => Wrap(() => { svc.Restart(cluster, req.Namespace ?? "default", req.Deployment ?? ""); return "restarted"; }),
            "rollout_status" => svc.RolloutStatus(cluster, req.Namespace ?? "default", req.Deployment ?? ""),
            "deploy" => svc.Deploy(cluster, req.Template ?? "", req.Name ?? "",
                req.Namespace ?? "default", null, 1),
            "templates" => K8sTemplateCatalog.Templates.Select(t => new { t.Id, t.Name, t.Arch, t.Kind }),
            "remove" => Wrap(() =>
            {
                var (d, s) = svc.Remove(cluster, req.Name ?? "", req.Namespace ?? "default", req.Confirm);
                return $"removed deployments={d} services={s}";
            }),
            _ => throw new ArgumentException($"未知操作: {req.Op}"),
        };
        if (req.Op is "scale" or "restart" or "deploy" or "remove")
            audit.Append(new AuditEvent { User = http.AuthUser(),
                Role = roleStr, Action = "k8s.op", Target = $"{cluster}/{req.Op}/{req.Deployment ?? req.Name}",
                Ip = http.ClientIp(), Result = "ok" });
        return Results.Json(result);
    }
    catch (ArgumentException ex) { return Results.Json(new { error = ex.Message }, statusCode: 400); }
    catch (KeyNotFoundException) { return Results.Json(new { error = "cluster not found" }, statusCode: 404); }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 502); }
});
static object Wrap(Func<object> f) => f();
// ---- FR-A28 K8s 集成端点结束 ----
// FR-A18 负载均衡专用端点：按操作分级鉴权（LbPolicy），经下发桥审计留痕。
app.MapPost("/api/nodes/{id}/lb", async (string id, LbOpRequest req, HttpContext http, NodeCommandBridge bridge) =>
{
    var roleStr = http.AuthRole();
    if (!Enum.TryParse<UserRole>(roleStr, out var role) || !LbPolicy.Allows(req.Op ?? "", role))
    {
        return Results.Json(new { error = "insufficient role for lb op" }, statusCode: 403);
    }
    var args = LbOpArgs.Build(req);
    try
    {
        var outcome = await bridge.DispatchAsync(id, "lb.manage", args, req.TimeoutMs,
            http.AuthUser(), roleStr,
            http.ClientIp());
        return Results.Json(new { exit_code = outcome.ExitCode, output = outcome.Output, request_id = outcome.RequestId });
    }
    catch (BridgeException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
});
// FR-A17 日志清理专用端点：按操作分级鉴权（LogCleanPolicy），经下发桥审计留痕。
app.MapPost("/api/nodes/{id}/logclean", async (string id, LogCleanOpRequest req, HttpContext http, NodeCommandBridge bridge) =>
{
    var roleStr = http.AuthRole();
    if (!Enum.TryParse<UserRole>(roleStr, out var role) || !LogCleanPolicy.Allows(req.Op ?? "", role))
    {
        return Results.Json(new { error = "insufficient role for logclean op" }, statusCode: 403);
    }
    var args = LogCleanOpArgs.Build(req);
    try
    {
        var outcome = await bridge.DispatchAsync(id, "logclean.manage", args, req.TimeoutMs,
            http.AuthUser(), roleStr,
            http.ClientIp());
        return Results.Json(new { exit_code = outcome.ExitCode, output = outcome.Output, request_id = outcome.RequestId });
    }
    catch (BridgeException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
});
// FR-A15 MySQL 主从复制专用端点：按操作分级鉴权（DbPolicy），经下发桥审计留痕。
app.MapPost("/api/nodes/{id}/db", async (string id, DbReplicaRequest req, HttpContext http, NodeCommandBridge bridge) =>
{
    var roleStr = http.AuthRole();
    if (!Enum.TryParse<UserRole>(roleStr, out var role) || !DbPolicy.Allows(req.Op ?? "", role))
    {
        return Results.Json(new { error = "insufficient role for db replica op" }, statusCode: 403);
    }
    var args = DbReplicaArgs.Build(req);
    try
    {
        var outcome = await bridge.DispatchAsync(id, "db.manage", args, req.TimeoutMs,
            http.AuthUser(), roleStr,
            http.ClientIp());
        return Results.Json(new { exit_code = outcome.ExitCode, output = outcome.Output, request_id = outcome.RequestId });
    }
    catch (BridgeException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
});
// 数据库基础操作专用端点（backlog B2 切真）：按操作分级鉴权（DbPolicy），经下发桥审计留痕。
app.MapPost("/api/nodes/{id}/db/ops", async (string id, DbOpRequest req, HttpContext http, NodeCommandBridge bridge) =>
{
    var roleStr = http.AuthRole();
    if (!Enum.TryParse<UserRole>(roleStr, out var role) || !DbPolicy.Allows(req.Op ?? "", role))
    {
        return Results.Json(new { error = "insufficient role for db op" }, statusCode: 403);
    }
    var args = DbOpArgs.Build(req);
    try
    {
        var outcome = await bridge.DispatchAsync(id, "db.manage", args, req.TimeoutMs,
            http.AuthUser(), roleStr,
            http.ClientIp());
        return Results.Json(new { exit_code = outcome.ExitCode, output = outcome.Output, request_id = outcome.RequestId });
    }
    catch (BridgeException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
});
// FTP 专用端点（backlog B2 切真）：按操作分级鉴权（FtpPolicy），经下发桥审计留痕。
app.MapPost("/api/nodes/{id}/ftp", async (string id, FtpOpRequest req, HttpContext http, NodeCommandBridge bridge) =>
{
    var roleStr = http.AuthRole();
    if (!Enum.TryParse<UserRole>(roleStr, out var role) || !FtpPolicy.Allows(req.Op ?? "", role))
    {
        return Results.Json(new { error = "insufficient role for ftp op" }, statusCode: 403);
    }
    var args = FtpOpArgs.Build(req);
    try
    {
        var outcome = await bridge.DispatchAsync(id, "ftp.manage", args, req.TimeoutMs,
            http.AuthUser(), roleStr,
            http.ClientIp());
        return Results.Json(new { exit_code = outcome.ExitCode, output = outcome.Output, request_id = outcome.RequestId });
    }
    catch (BridgeException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
});
// 环境变量（运行环境）专用端点（backlog B2 切真）：按操作分级鉴权（EnvPolicy），经下发桥审计留痕。
app.MapPost("/api/nodes/{id}/env", async (string id, EnvOpRequest req, HttpContext http, NodeCommandBridge bridge) =>
{
    var roleStr = http.AuthRole();
    if (!Enum.TryParse<UserRole>(roleStr, out var role) || !EnvPolicy.Allows(req.Op ?? "", role))
    {
        return Results.Json(new { error = "insufficient role for env op" }, statusCode: 403);
    }
    var args = EnvOpArgs.Build(req);
    try
    {
        var outcome = await bridge.DispatchAsync(id, "env.manage", args, req.TimeoutMs,
            http.AuthUser(), roleStr,
            http.ClientIp());
        return Results.Json(new { exit_code = outcome.ExitCode, output = outcome.Output, request_id = outcome.RequestId });
    }
    catch (BridgeException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
});
// FR-A21 SSL 证书端点：按操作分级鉴权（SslPolicy），经下发桥审计留痕。
app.MapPost("/api/nodes/{id}/ssl", async (string id, SslOpRequest req, HttpContext http, NodeCommandBridge bridge) =>
{
    var roleStr = http.AuthRole();
    if (!Enum.TryParse<UserRole>(roleStr, out var role) || !SslPolicy.Allows(req.Op ?? "", role))
    {
        return Results.Json(new { error = "insufficient role for ssl op" }, statusCode: 403);
    }
    var args = SslOpArgs.Build(req);
    try
    {
        var outcome = await bridge.DispatchAsync(id, "ssl.manage", args, req.TimeoutMs,
            http.AuthUser(), roleStr,
            http.ClientIp());
        return Results.Json(new { exit_code = outcome.ExitCode, output = outcome.Output, request_id = outcome.RequestId });
    }
    catch (BridgeException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
});
app.MapPost("/api/nodes/{id}/db/grants", async (string id, DbGrantRequest req, HttpContext http, NodeCommandBridge bridge) =>
{
    var roleStr = http.AuthRole();
    if (!Enum.TryParse<UserRole>(roleStr, out var role) || !DbPolicy.Allows(req.Op ?? "", role))
    {
        return Results.Json(new { error = "insufficient role for db grant op" }, statusCode: 403);
    }
    var args = DbGrantArgs.Build(req);
    try
    {
        var outcome = await bridge.DispatchAsync(id, "db.manage", args, req.TimeoutMs,
            http.AuthUser(), roleStr,
            http.ClientIp());
        return Results.Json(new { exit_code = outcome.ExitCode, output = outcome.Output, request_id = outcome.RequestId });
    }
    catch (BridgeException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
});
app.MapPost("/api/nodes/{id}/backup/policy", async (string id, BackupPolicyRequest req, HttpContext http, NodeCommandBridge bridge) =>
{
    var roleStr = http.AuthRole();
    if (!Enum.TryParse<UserRole>(roleStr, out var role) || !BackupPolicyOps.Allows(req.Op ?? "", role))
    {
        return Results.Json(new { error = "insufficient role for backup policy op" }, statusCode: 403);
    }
    var args = BackupPolicyArgs.Build(req);
    try
    {
        var outcome = await bridge.DispatchAsync(id, "backup.manage", args, req.TimeoutMs,
            http.AuthUser(), roleStr,
            http.ClientIp());
        return Results.Json(new { exit_code = outcome.ExitCode, output = outcome.Output, request_id = outcome.RequestId });
    }
    catch (BridgeException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
});
app.MapPost("/api/nodes/{id}/files/edit", async (string id, FileEditRequest req, HttpContext http, NodeCommandBridge bridge) =>
{
    var roleStr = http.AuthRole();
    if (!Enum.TryParse<UserRole>(roleStr, out var role) || !FilesPolicy.Allows(req.Op ?? "", role))
    {
        return Results.Json(new { error = "insufficient role for files op" }, statusCode: 403);
    }
    var args = FileEditArgs.Build(req);
    try
    {
        var outcome = await bridge.DispatchAsync(id, "files.manage", args, req.TimeoutMs,
            http.AuthUser(), roleStr,
            http.ClientIp());
        return Results.Json(new { exit_code = outcome.ExitCode, output = outcome.Output, request_id = outcome.RequestId });
    }
    catch (BridgeException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
});
app.MapPost("/api/nodes/{id}/cmd", async (string id, NodeCmdRequest req, HttpContext http, NodeCommandBridge bridge) =>
{
    try
    {
        var outcome = await bridge.DispatchAsync(id, req.Command, req.Args, req.TimeoutMs,
            http.AuthUser(), http.AuthRole(),
            http.ClientIp());
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
            var dataDir = TaoOpsPaths.DataDir;
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
public sealed record ChangePasswordRequest(string? OldPassword, string? NewPassword);
public sealed record NodeCmdRequest(string? Command, IReadOnlyList<string>? Args, int TimeoutMs);

// ---- FR-A11 Docker 下发请求（backlog A 增加速器/仓库登录字段）----
public sealed record DockerOpRequest(string? Op, string? Name, string? Ref, string? Command, int? Tail,
    string? Addr, string? Registry, string? Username, string? Password, int TimeoutMs);

// ---- FR-A30 多机批量下发请求 ----
public sealed record BatchOpRequest(string? Module, IReadOnlyList<string?>? Args, IReadOnlyList<string>? NodeIds);

// ---- FR-A29 外接模式配置请求 ----
public sealed record ExternalConfigRequest(string? Endpoint, string? ApiKey, IReadOnlyList<string>? SsrfWhitelist);

// ---- FR-A12 网站统计下发请求 ----
public sealed record WebStatsOpRequest(string? Op, string? Site, string? Path, string? From, string? To, int TimeoutMs);

// ---- 计划任务下发请求（backlog B2 切真）----
public sealed record CronOpRequest(string? Op, string? Name, string? Schedule, string? Command, int TimeoutMs);
public sealed record FwOpRequest(string? Op, int? Port, string? Proto, int TimeoutMs);
public sealed record TamperGuardOpRequest(string? Op, int TimeoutMs);

// ---- 网站下发请求（backlog B2 切真）----
public sealed record SiteOpRequest(string? Op, string? Name, string? Domains, string? Proxy, int? Port, int TimeoutMs);

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

internal static class CronOpArgs
{
    /// <summary>把请求字段摊平成 key=value 参数（Agent 侧 parseKeyValues 约定）。</summary>
    public static IReadOnlyList<string> Build(CronOpRequest req)
    {
        var args = new List<string> { $"op={req.Op}" };
        if (!string.IsNullOrEmpty(req.Name)) args.Add($"name={req.Name}");
        if (!string.IsNullOrEmpty(req.Schedule)) args.Add($"schedule={req.Schedule}");
        if (!string.IsNullOrEmpty(req.Command)) args.Add($"command={req.Command}");
        return args;
    }
}

internal static class FwOpArgs
{
    /// <summary>把请求字段摊平成 key=value 参数（Agent 侧 parseKeyValues 约定）。</summary>
    public static IReadOnlyList<string> Build(FwOpRequest req)
    {
        var args = new List<string> { $"op={req.Op}" };
        if (req.Port is { } port) args.Add($"port={port}");
        if (!string.IsNullOrEmpty(req.Proto)) args.Add($"proto={req.Proto}");
        return args;
    }
}

internal static class SiteOpArgs
{
    /// <summary>把请求字段摊平成 key=value 参数（Agent 侧 parseKeyValues 约定）。</summary>
    public static IReadOnlyList<string> Build(SiteOpRequest req)
    {
        var args = new List<string> { $"op={req.Op}" };
        if (!string.IsNullOrEmpty(req.Name)) args.Add($"name={req.Name}");
        if (!string.IsNullOrEmpty(req.Domains)) args.Add($"domains={req.Domains}");
        if (!string.IsNullOrEmpty(req.Proxy)) args.Add($"proxy={req.Proxy}");
        if (req.Port is int port) args.Add($"port={port}");
        return args;
    }
}

internal static class DockerOpArgs
{
    /// <summary>把请求字段摊平成 key=value 参数（Agent 侧 parseKeyValues 约定）。
    /// registry_login 的密码经 stdin 下发，不进审计（审计只记 user/role/action/result）。</summary>
    public static IReadOnlyList<string> Build(DockerOpRequest req)
    {
        var args = new List<string> { $"op={req.Op}" };
        if (!string.IsNullOrEmpty(req.Name)) args.Add($"name={req.Name}");
        if (!string.IsNullOrEmpty(req.Ref)) args.Add($"ref={req.Ref}");
        if (!string.IsNullOrEmpty(req.Command)) args.Add($"command={req.Command}");
        if (!string.IsNullOrEmpty(req.Addr)) args.Add($"addr={req.Addr}");
        if (!string.IsNullOrEmpty(req.Registry)) args.Add($"registry={req.Registry}");
        if (!string.IsNullOrEmpty(req.Username)) args.Add($"username={req.Username}");
        if (!string.IsNullOrEmpty(req.Password)) args.Add($"password={req.Password}");
        if (req.Tail is int tail) args.Add($"tail={tail}");
        return args;
    }
}
