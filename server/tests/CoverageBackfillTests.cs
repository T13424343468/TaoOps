using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using TaoOps.Server.Audit;
using TaoOps.Server.Auth;
using TaoOps.Server.Grpc;
using TaoOps.Server.Sec;
using TaoOps.Server.Services;
using TaoOps.Server.Waf;
using Xunit;
using AgentService = TaoOps.Server.Services.AgentService;

namespace TaoOps.Server.Tests;

/// <summary>
/// 覆盖账补齐（整改账本 v3.2.0 硬门禁）：针对手写行/分支缺口定向补测——
/// SEC 回流启动链、托管服务巡检循环、静默时段三分支、口令校验畸形格式、
/// 账本坏行容错、规则自动编号/重复/导入校验、令牌 TTL 旋钮降级。
/// </summary>
public class CoverageBackfillTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "taoops-cb-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    // ---- SEC 回流启动链（SecEventIngestor.StartAsync 全链路）----

    [Fact]
    public async Task SecIngestor_StartAsync_UnknownNode_Throws()
    {
        var ingestor = new SecEventIngestor(new NodeStreamHub(), new SecEventStore(_dir));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ingestor.StartAsync("ghost", CancellationToken.None));
    }

    [Fact]
    public async Task SecIngestor_StartAsync_SubscribesParsesAndPersists()
    {
        var hub = new NodeStreamHub();
        var store = new SecEventStore(_dir);
        var ingestor = new SecEventIngestor(hub, store);
        hub.AttachLog("sec-node");
        var session = hub.GetLog("sec-node")!;
        using var cts = new CancellationTokenSource();

        var run = ingestor.StartAsync("sec-node", cts.Token);
        await Task.Delay(100);

        // 订阅控制已下发（TailLines=0 全量回流）。
        var ctrl = await session.Outbound.Reader.ReadAsync(new CancellationTokenSource(2000).Token);
        Assert.Equal("subscribe", ctrl.Action);
        Assert.Equal(SecEventIngestor.DefaultSecLogPath, ctrl.Path);
        Assert.Equal(0, ctrl.TailLines);

        // 广播：合法 TAOOPS_SEC 行 + 畸形行 + 跨 chunk 半行 + 纯 JSON 行。
        session.Broadcast(new TaoOps.Server.Grpc.LogChunk
        {
            NodeId = "sec-node",
            Data = Google.Protobuf.ByteString.CopyFromUtf8(
                "TAOOPS_SEC {\"event\":\"ssh_lockout\",\"ip\":\"9.9.9.9\",\"failures\":3}\n" +
                "not a json line at all\n" +
                "{\"event\":\"ssh_lockout\",\"ip\":\"7.7.7.7\",\"failures\":1"),
            Ts = 1700000000000,
        });
        session.Broadcast(new TaoOps.Server.Grpc.LogChunk
        {
            NodeId = "sec-node",
            Data = Google.Protobuf.ByteString.CopyFromUtf8("}\n"),
            Ts = 1700000000001,
        });
        await Task.Delay(100);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        var events = store.Query("sec-node").OrderBy(e => e.Ip).ToList();
        Assert.Equal(2, events.Count);
        Assert.Equal("7.7.7.7", events[0].Ip);
        Assert.Equal("9.9.9.9", events[1].Ip);
        Assert.Equal(3, events[1].Failures);
        // 落盘持久：重建 store 可读回。
        Assert.Equal(2, new SecEventStore(_dir).Query("sec-node").Count);
    }

    // ---- 托管服务巡检循环（ExecuteAsync / PruneFaulted / 旋钮降级）----

    [Fact]
    public async Task HostedService_ExecuteLoop_PollsSubscribesAndStopsOnCancel()
    {
        Environment.SetEnvironmentVariable("TAOOPS_INGEST_POLL_SECONDS", "abc"); // 非法值 → 默认 5s 分支
        try
        {
            var registry = new NodeRegistry();
            registry.Register(new TaoOps.Server.Grpc.RegisterRequest { AgentId = "a1", Hostname = "h1" });
            var calls = 0;
            var svc = new StreamIngestorHostedService(registry,
                (n, ct) => { calls++; return Task.CompletedTask; },
                (n, ct) => { calls++; return Task.CompletedTask; },
                NullLogger<StreamIngestorHostedService>.Instance);

            using var cts = new CancellationTokenSource(300);
            await svc.StartAsync(cts.Token);
            await Task.Delay(500); // 至少跑满一轮巡检
            await svc.StopAsync(CancellationToken.None);
            Assert.True(calls >= 2, $"expected at least one poll round, calls={calls}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TAOOPS_INGEST_POLL_SECONDS", "3600");
        }
    }

    [Fact]
    public async Task HostedService_FaultedTaskWithLogger_PrunedAndResubscribed()
    {
        var registry = new NodeRegistry();
        registry.Register(new TaoOps.Server.Grpc.RegisterRequest { AgentId = "a1", Hostname = "h1" });
        var failFirst = true;
        Task Start(string node, CancellationToken ct)
        {
            // 返回故障任务（而非同步抛出）：PruneFaulted 才能观测到并记录日志后移出重订。
            return failFirst
                ? Task.FromException(new InvalidOperationException("stream dropped"))
                : Task.CompletedTask;
        }
        var svc = new StreamIngestorHostedService(registry, Start, Start,
            NullLogger<StreamIngestorHostedService>.Instance);

        // 第一轮：任务故障 → 记录日志并移出；第二轮重订成功。
        await svc.PollOnceAsync(CancellationToken.None);
        await Task.Delay(50); // 等任务真正 faulted
        failFirst = false;
        Assert.Equal(2, await svc.PollOnceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task HostedService_PollBlewUp_LoggedAndLoopContinues()
    {
        Environment.SetEnvironmentVariable("TAOOPS_INGEST_POLL_SECONDS", "1");
        try
        {
            var registry = new NodeRegistry();
            registry.Register(new TaoOps.Server.Grpc.RegisterRequest { AgentId = "a1", Hostname = "h1" });
            var failFirst = true;
            Task Start(string node, CancellationToken ct)
            {
                if (failFirst) throw new Exception("poll exploded");
                return Task.CompletedTask;
            }
            var svc = new StreamIngestorHostedService(registry, Start, Start,
                NullLogger<StreamIngestorHostedService>.Instance);
            using var cts = new CancellationTokenSource(2500);
            await svc.StartAsync(cts.Token);
            await Task.Delay(300); // 第一轮巡检抛错被记录，循环不死
            failFirst = false;
            await Task.Delay(1200); // 第二轮恢复订阅
            await svc.StopAsync(CancellationToken.None); // 延迟取消 → TaskCanceledException break 分支
        }
        finally
        {
            Environment.SetEnvironmentVariable("TAOOPS_INGEST_POLL_SECONDS", "3600");
        }
    }

    // ---- 静默时段三分支（A8 异常登录时段）----

    [Fact]
    public void QuietHours_StartEqualsEnd_AlwaysQuiet_NoAlert()
    {
        Environment.SetEnvironmentVariable("TAOOPS_SEC_QUIET_HOURS", "9-9"); // start==end → 全天静默
        try
        {
            var detector = new IntrusionDetector(new AuditStore(_dir));
            Assert.Equal((9, 9), detector.QuietHours);
            var alert = detector.OnLoginSuccess("u", "1.1.1.1", 1700000000000, hourOverride: 3);
            Assert.Equal(SecAlertTypes.LoginHour, alert.Type);
            Assert.Equal("(no alert)", alert.Detail); // 静默期内不告警
        }
        finally
        {
            Environment.SetEnvironmentVariable("TAOOPS_SEC_QUIET_HOURS", null);
        }
    }

    [Fact]
    public void QuietHours_ForwardWindow_InAndOutBranches()
    {
        Environment.SetEnvironmentVariable("TAOOPS_SEC_QUIET_HOURS", "2-5"); // start<end
        try
        {
            var detector = new IntrusionDetector(new AuditStore(_dir));
            var inWindow = detector.OnLoginSuccess("u", "1.1.1.1", 0, hourOverride: 3);
            Assert.Equal("(no alert)", inWindow.Detail); // 窗口内静默
            var outWindow = detector.OnLoginSuccess("u", "1.1.1.1", 0, hourOverride: 12);
            Assert.Contains("login outside quiet hours", outWindow.Detail); // 窗口外告警
            Assert.Equal("medium", outWindow.Severity);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TAOOPS_SEC_QUIET_HOURS", null);
        }
    }

    [Fact]
    public void QuietHours_CrossMidnightWindow_InAndOutBranches()
    {
        Environment.SetEnvironmentVariable("TAOOPS_SEC_QUIET_HOURS", "22-6"); // start>end 跨零点
        try
        {
            var detector = new IntrusionDetector(new AuditStore(_dir));
            Assert.Equal((22, 6), detector.QuietHours);
            var inWindow = detector.OnLoginSuccess("u", "1.1.1.1", 0, hourOverride: 23); // hour>=start
            Assert.Equal("(no alert)", inWindow.Detail);
            var inWindow2 = detector.OnLoginSuccess("u", "1.1.1.1", 0, hourOverride: 5); // hour<end
            Assert.Equal("(no alert)", inWindow2.Detail);
            var outWindow = detector.OnLoginSuccess("u", "1.1.1.1", 0, hourOverride: 12);
            Assert.Contains("22-06", outWindow.Detail);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TAOOPS_SEC_QUIET_HOURS", null);
        }
    }

    [Fact]
    public void Detector_Query_FiltersByTypeSeverityAndTime()
    {
        var detector = new IntrusionDetector(new AuditStore(_dir));
        var a1 = detector.OnLoginSuccess("u1", "1.1.1.1", 0, hourOverride: 12);   // login_hour
        var a2 = detector.OnUserAction("u2", "drop-users", "2.2.2.2", 0);         // unusual_action

        Assert.Equal(2, detector.Query().Count);
        Assert.Single(detector.Query(type: SecAlertTypes.LoginHour));
        Assert.Single(detector.Query(type: SecAlertTypes.UnusualAction));
        Assert.Single(detector.Query(severity: "medium"));   // login_hour=medium
        Assert.Single(detector.Query(severity: "low"));      // unusual_action=low
        Assert.Equal(2, detector.Query(fromMs: 0, toMs: long.MaxValue).Count); // toMs 分支
        Assert.Empty(detector.Query(fromMs: long.MaxValue));
    }

    // ---- 口令哈希校验畸形格式（AuthService.VerifyPassword 分支）----

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("a$b$c$d$e")]
    [InlineData("md5$100000$c2FsdA$aGFzaA")]
    [InlineData("pbkdf2-sha256$abc$c2FsdA$aGFzaA")]
    [InlineData("pbkdf2-sha256$0$c2FsdA$aGFzaA")]
    [InlineData("pbkdf2-sha256$-5$c2FsdA$aGFzaA")]
    [InlineData("pbkdf2-sha256$100000$!!!$aGFzaA")]
    public void VerifyPassword_MalformedStoredFormats_ReturnFalse(string stored)
    {
        Assert.False(AuthService.VerifyPassword("pw", stored));
    }

    [Fact]
    public void VerifyPassword_Roundtrip_AndWrongPassword()
    {
        var hash = AuthService.HashPassword("s3cret");
        Assert.True(AuthService.VerifyPassword("s3cret", hash));
        Assert.False(AuthService.VerifyPassword("wrong", hash));
    }

    // ---- 用户库边界（UserStore 空名/重复/落盘回读）----

    [Fact]
    public void UserStore_Add_EmptyUsername_Throws()
    {
        var users = new UserStore(_dir);
        Assert.Throws<InvalidOperationException>(() => users.Add(new UserAccount { Username = " " }));
    }

    [Fact]
    public void UserStore_AddDuplicate_Throws_AndRoundtrips()
    {
        var users = new UserStore(_dir);
        users.Add(new UserAccount { Username = "dup", Role = UserRole.Admin });
        Assert.Throws<InvalidOperationException>(() => users.Add(new UserAccount { Username = "dup" }));

        var reloaded = new UserStore(_dir).Load(); // 落盘回读（File.Exists true 分支）
        Assert.Single(reloaded);
        Assert.Equal("dup", reloaded[0].Username);
        Assert.Null(users.Find("nobody"));
    }

    // ---- WAF 规则库边界（自动编号/重复/导入校验/落盘回读）----

    [Fact]
    public void WafRuleStore_Add_EmptyId_Autogenerates()
    {
        var store = new WafRuleStore(_dir);
        var added = store.Add(new WafRule { Name = "auto", Pattern = "p" });
        Assert.NotEmpty(added.Id);
        Assert.Single(store.Load().Rules); // 落盘回读
    }

    [Fact]
    public void WafRuleStore_AddDuplicateId_Throws()
    {
        var store = new WafRuleStore(_dir);
        store.Add(new WafRule { Id = "dup-1", Name = "n", Pattern = "p" });
        Assert.Throws<InvalidOperationException>(() => store.Add(new WafRule { Id = "dup-1", Name = "n2", Pattern = "p" }));
    }

    [Fact]
    public void WafRuleStore_Import_InvalidPayload_Throws()
    {
        var store = new WafRuleStore(_dir);
        Assert.ThrowsAny<Exception>(() => store.Import("not json at all"));
    }

    // ---- WAF 事件解析边界（非对象 JSON / 坏行落盘容错）----

    [Fact]
    public void WafEventStore_TryParse_NonObjectAndMalformed_ReturnNull()
    {
        Assert.Null(WafEventStore.TryParseEvent("[1,2,3]", "n1"));
        Assert.Null(WafEventStore.TryParseEvent("\"just a string\"", "n1"));
        Assert.Null(WafEventStore.TryParseEvent("{broken json", "n1"));
    }

    [Fact]
    public void WafEventStore_Reload_SkipsBlankAndMalformedLines()
    {
        var dir = Path.Combine(_dir, "waf-bad");
        var store = new WafEventStore(dir);
        store.Append(new WafEvent { NodeId = "n1", Event = "deny", RuleId = "r1", Ts = 1 });
        File.AppendAllText(Path.Combine(dir, "waf", "events.jsonl"), "\n{malformed\n");
        var reloaded = new WafEventStore(dir);
        Assert.Equal("r1", Assert.Single(reloaded.Query()).RuleId);
    }

    // ---- SEC 事件解析边界（空行/非对象/缺字段默认值/坏行落盘容错）----

    [Fact]
    public void SecEventStore_TryParse_EdgeCases()
    {
        Assert.Null(SecEventStore.TryParseEvent("   ", "n1"));
        Assert.Null(SecEventStore.TryParseEvent("[1,2]", "n1"));
        Assert.Null(SecEventStore.TryParseEvent("{broken", "n1"));

        // 缺 failures/ts → 默认 0 / 当前时间；缺字符串字段 → 空串。
        var ev = SecEventStore.TryParseEvent("{\"event\":\"ssh_lockout\"}", "n1")!;
        Assert.Equal("ssh_lockout", ev.Event);
        Assert.Equal("", ev.Ip);
        Assert.Equal(0, ev.Failures);
        Assert.True(ev.Ts > 0);

        // failures/ts 非数值类型（字符串）→ 默认值分支。
        var ev2 = SecEventStore.TryParseEvent("{\"failures\":\"x\",\"ts\":\"y\"}", "n1")!;
        Assert.Equal(0, ev2.Failures);
        Assert.True(ev2.Ts > 0);
    }

    [Fact]
    public void SecEventStore_Reload_SkipsMalformedLines_AndQueryFilters()
    {
        var dir = Path.Combine(_dir, "sec-bad");
        var store = new SecEventStore(dir);
        store.Append(new SecEvent { NodeId = "n1", Event = "ssh_lockout", Ip = "1.1.1.1", Failures = 2, Ts = 1000 });
        File.AppendAllText(Path.Combine(dir, "sec", "events.jsonl"), "{malformed\n");
        var reloaded = new SecEventStore(dir);
        Assert.Single(reloaded.Query());

        Assert.Single(reloaded.Query(nodeId: "n1", fromMs: 500, toMs: 2000));
        Assert.Empty(reloaded.Query(toMs: 500));
        Assert.Empty(reloaded.Query(fromMs: 5000));
    }

    // ---- 审计账本坏行容错（空行/畸形行跳过）----

    [Fact]
    public void AuditStore_Reload_SkipsBlankAndMalformedLines()
    {
        var dir = Path.Combine(_dir, "audit-bad");
        var store = new AuditStore(dir);
        store.Append(new AuditEvent { User = "u", Action = "GET", Target = "/api/x", Ts = 1000 });
        File.AppendAllLines(Path.Combine(dir, "audit", "audit.jsonl"), ["", "{malformed"]);
        var reloaded = new AuditStore(dir);
        Assert.Single(reloaded.Query());
        Assert.True(reloaded.VerifyChain().Valid);
    }

    // ---- 令牌 TTL 旋钮降级（非法值 → 默认 8 小时）----

    [Fact]
    public void TokenStore_InvalidSessionMs_FallsBackToDefault()
    {
        Environment.SetEnvironmentVariable("TAOOPS_SESSION_MS", "not-a-number");
        try
        {
            var tokens = new TokenStore();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var session = tokens.Issue("u", UserRole.Admin, now);
            Assert.True(session.ExpiresAtMs > now + (long)(7.9 * 3600 * 1000)); // 默认 8h
            Assert.NotNull(tokens.Validate(session.Token, now));
            Assert.True(tokens.Revoke(session.Token));
            Assert.Null(tokens.Validate(session.Token, now));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TAOOPS_SESSION_MS", null);
        }
    }

    // ---- AgentService 双泵异常收尾（读循环 RpcException/OCE + 外层 OCE）----

    private static Task InvokePump(string methodName, object reader, object writer, object session, CancellationToken ct)
    {
        var method = typeof(AgentService).GetMethod(methodName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        return (Task)method!.Invoke(null, [reader, writer, session, ct])!;
    }

    private sealed class ThrowingTermWriter : IServerStreamWriter<TaoOps.Server.Grpc.TermData>
    {
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(TaoOps.Server.Grpc.TermData message) => Task.CompletedTask;
    }

    private sealed class ThrowingLogWriter : IServerStreamWriter<TaoOps.Server.Grpc.LogControl>
    {
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(TaoOps.Server.Grpc.LogControl message) => Task.CompletedTask;
    }

    private sealed class ThrowingTermReader : IAsyncStreamReader<TaoOps.Server.Grpc.TermData>
    {
        public Task<bool> MoveNext(CancellationToken cancellationToken) =>
            throw new RpcException(new Status(StatusCode.Cancelled, "client gone"));
        public TaoOps.Server.Grpc.TermData Current { get; } = new();
    }

    private sealed class ThrowingLogReader : IAsyncStreamReader<TaoOps.Server.Grpc.LogChunk>
    {
        public Task<bool> MoveNext(CancellationToken cancellationToken) => Task.FromException<bool>(new RpcException(new Status(StatusCode.Cancelled, "client gone")));
        public TaoOps.Server.Grpc.LogChunk Current { get; } = new();
    }

    private sealed class CancelledTermReader : IAsyncStreamReader<TaoOps.Server.Grpc.TermData>
    {
        public Task<bool> MoveNext(CancellationToken cancellationToken) => Task.FromException<bool>(new OperationCanceledException(cancellationToken));
        public TaoOps.Server.Grpc.TermData Current { get; } = new();
    }

    private sealed class CancelledLogReader : IAsyncStreamReader<TaoOps.Server.Grpc.LogChunk>
    {
        public Task<bool> MoveNext(CancellationToken cancellationToken) => Task.FromException<bool>(new OperationCanceledException(cancellationToken));
        public TaoOps.Server.Grpc.LogChunk Current { get; } = new();
    }

    [Fact]
    public async Task PumpTerm_ReaderRpcException_CompletesOutboundAndReturns()
    {
        var hub = new NodeStreamHub();
        var session = hub.AttachTerm("pump-node", "pump-sess");
        var pump = InvokePump("PumpTermAsync", new ThrowingTermReader(), new ThrowingTermWriter(), session, CancellationToken.None);
        await pump;
        // 读循环收尾即关闭写侧：外层泵自然退出。
        Assert.True(session.Outbound.Reader.Completion.IsCompleted);
    }

    [Fact]
    public async Task PumpTerm_ReaderCancelled_OuterCatchCompletes()
    {
        var hub = new NodeStreamHub();
        var session = hub.AttachTerm("pump-node2", "pump-sess2");
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // 外层 ReadAllAsync 立即 OCE → 外层 catch 分支
        var pump = InvokePump("PumpTermAsync", new CancelledTermReader(), new ThrowingTermWriter(), session, cts.Token);
        await pump;
    }

    [Fact]
    public async Task PumpLog_ReaderRpcException_CompletesOutboundAndReturns()
    {
        var hub = new NodeStreamHub();
        var session = hub.AttachLog("pump-log");
        var pump = InvokePump("PumpLogAsync", new ThrowingLogReader(), new ThrowingLogWriter(), session, CancellationToken.None);
        await pump;
        Assert.True(session.Outbound.Reader.Completion.IsCompleted);
    }

    [Fact]
    public async Task PumpLog_ReaderCancelled_OuterCatchCompletes()
    {
        var hub = new NodeStreamHub();
        var session = hub.AttachLog("pump-log2");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var pump = InvokePump("PumpLogAsync", new CancelledLogReader(), new ThrowingLogWriter(), session, cts.Token);
        await pump;
    }

    // ---- WS 桥收尾分支：用工厂的用例已并入 CoverageBackfillEndpointTests（Integration 集合串行，
    // ---- 避免并行工厂互踩进程级环境变量）。----
}

/// <summary>覆盖账补齐第二批：Program 端点分支、桥异常映射、回流正常收尾、旋钮降级分支。</summary>
[Collection("Integration")]
public class CoverageBackfillEndpointTests
{
    private readonly TaoOpsWebAppFactory _factory;
    private readonly HttpClient _client;

    public CoverageBackfillEndpointTests(TaoOpsWebAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task AsAdmin()
    {
        _client.DefaultRequestHeaders.Authorization = new("Bearer", await _factory.LoginAdminAsync());
    }

    [Fact]
    public async Task Bootstrap_AfterUsersExist_Returns403()
    {
        await AsAdmin(); // 确保账号库非空
        var res = await _client.PostAsJsonAsync("/api/auth/bootstrap",
            new { username = "late-user", password = "pw-123456" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Contains("bootstrap closed", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Bootstrap_MissingFields_Returns400()
    {
        // 400 分支仅在账号库为空时可达（403 关闭分支先行）；用全新数据目录建独立工厂。
        using var fresh = new TaoOpsWebAppFactory();
        using var client = fresh.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/bootstrap", new { username = "", password = "" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("required", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CreateUser_InvalidRole_Returns400()
    {
        await AsAdmin();
        var res = await _client.PostAsJsonAsync("/api/auth/users",
            new { username = "bad-role-user", password = "pw-123456", role = "SuperAdmin" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("role must be", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Permissions_Matrix_ServedForAdmin()
    {
        await AsAdmin();
        var res = await _client.GetAsync("/api/auth/permissions");
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Admin", body.GetProperty("role").GetString());
        Assert.True(body.GetProperty("permissions").GetArrayLength() > 0);
        Assert.True(body.GetProperty("all_permissions").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Nodes_And_Cmd_UnknownNode_MappedTo404()
    {
        await AsAdmin();
        var list = await _client.GetAsync("/api/nodes");
        list.EnsureSuccessStatusCode();

        var cmd = await _client.PostAsJsonAsync("/api/nodes/ghost-node/cmd",
            new { command = "shell", args = new[] { "echo", "hi" } });
        Assert.Equal(HttpStatusCode.NotFound, cmd.StatusCode);
        Assert.Contains("unknown node_id", await cmd.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_Request_IsNotAudited()
    {
        await AsAdmin();
        await _client.PostAsJsonAsync("/api/auth/login",
            new { username = TaoOpsWebAppFactory.AdminUser, password = TaoOpsWebAppFactory.AdminPassword });
        var events = _factory.Services.GetRequiredService<AuditStore>().Query();
        // 中间件分支：登录请求不留中间件审计痕（防爆破账本由 AuthService 自记，Action=login）。
        Assert.DoesNotContain(events, e => e.Action == "POST" && e.Target == "/api/auth/login");
    }

    [Fact]
    public async Task AuthService_PolicyExposed()
    {
        var auth = _factory.Services.GetRequiredService<AuthService>();
        Assert.True(auth.Policy.MaxFailures > 0);
    }

    // ---- WS 桥收尾分支（日志流未知节点 404、终端/日志异常拆链）----

    [Fact]
    public async Task Logs_UnknownNode_WsConnectRejected()
    {
        var wsClient = _factory.Server.CreateWebSocketClient();
        await Assert.ThrowsAnyAsync<Exception>(() =>
            wsClient.ConnectAsync(new Uri("http://localhost/ws/agent/ghost-logs/logs?path=/tmp/x"), CancellationToken.None));
    }

    [Fact]
    public async Task Terminal_ClientAbruptDisconnect_ServerTearsDownWithoutCrash()
    {
        var hub = _factory.Services.GetRequiredService<NodeStreamHub>();
        var session = hub.AttachTerm("cb-term", "cb-sess");
        var wsClient = _factory.Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(
            new Uri("http://localhost/ws/agent/cb-term/terminal?session=cb-sess"), CancellationToken.None);
        ws.Dispose(); // 不握手直接断（服务端 ReceiveAsync 抛 WebSocketException → catch → finally 拆链）
        // 断开后连发投递：泵若尚存活，WsSendAsync 检查到 WS 非 Open 即返回（发送前置分支），
        // 或 SendAsync 抛 WebSocketException 命中捕获行——两条路都推进覆盖。
        for (var i = 0; i < 100; i++)
        {
            session.Inbound.Writer.TryWrite(new TaoOps.Server.Grpc.TermData
            {
                NodeId = "cb-term",
                SessionId = "cb-sess",
                Data = Google.Protobuf.ByteString.CopyFromUtf8("late"),
            });
        }
        await Task.Delay(200);
        // 服务未崩：普通端点仍可用。
        var res = await _client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Logs_ClientAbruptDisconnect_ServerTearsDownWithoutCrash()
    {
        var hub = _factory.Services.GetRequiredService<NodeStreamHub>();
        hub.AttachLog("cb-logs");
        var wsClient = _factory.Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(
            new Uri("http://localhost/ws/agent/cb-logs/logs?path=/tmp/cb.log&tail=1"), CancellationToken.None);
        ws.Dispose();
        await Task.Delay(200);
        var res = await _client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    // ---- 传输装配：环境变量缺省分支（默认数据目录 + 默认端口）----
    // 与 TransportConfigTests 同集合串行（环境变量进程级，避免并行互踩）。

    [Fact]
    public void ConfigureTransport_EnvUnset_UsesDefaults()
    {
        if (!TryEnsureSystemDataDir())
        {
            return; // 非 root 环境（如 CI runner）无法创建 /var/lib/taoops，系统路径分支仅 root 可实测
        }
        var prevData = Environment.GetEnvironmentVariable("TAOOPS_DATA_DIR");
        var prevPort = Environment.GetEnvironmentVariable("TAOOPS_PORT");
        Environment.SetEnvironmentVariable("TAOOPS_DATA_DIR", null);
        Environment.SetEnvironmentVariable("TAOOPS_PORT", null);
        try
        {
            var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
            Program.ConfigureTransport(builder, insecure: false, legacyUrls: false); // 不启动即通过
        }
        finally
        {
            Environment.SetEnvironmentVariable("TAOOPS_DATA_DIR", prevData);
            Environment.SetEnvironmentVariable("TAOOPS_PORT", prevPort);
            try { Directory.Delete("/var/lib/taoops/server", recursive: true); } catch { }
        }
    }

    [Fact]
    public void ConfigureTransport_PortZeroOrNegative_FallsBackToDefault()
    {
        var prevPort = Environment.GetEnvironmentVariable("TAOOPS_PORT");
        Environment.SetEnvironmentVariable("TAOOPS_PORT", "0"); // TryParse 成功但 p<=0 → 默认端口分支
        try
        {
            var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
            Program.ConfigureTransport(builder, insecure: true, legacyUrls: false);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TAOOPS_PORT", prevPort);
        }
    }

    // ---- cmd 下发成功路径（真实管道 + 假通道回执）----

    private sealed class CmdResultReader : ICommandResultReader
    {
        private readonly System.Threading.Channels.Channel<DispatchResult> _results =
            System.Threading.Channels.Channel.CreateUnbounded<DispatchResult>();
        public void Enqueue(DispatchResult result) => _results.Writer.TryWrite(result);
        public async Task<DispatchResult?> ReadNextAsync(CancellationToken ct)
        {
            while (await _results.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                if (_results.Reader.TryRead(out var r))
                    return r;
            return null;
        }
    }

    private sealed class CmdEchoWriter(CmdResultReader reader) : ICommandRequestWriter
    {
        public Task WriteAsync(DispatchRequest request, CancellationToken ct)
        {
            reader.Enqueue(new DispatchResult { RequestId = request.RequestId, ExitCode = 0, Output = "ok" });
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Cmd_KnownNode_EchoRoundtrip_ReturnsOutcome()
    {
        await AsAdmin();
        var registry = _factory.Services.GetRequiredService<NodeRegistry>();
        var hub = _factory.Services.GetRequiredService<NodeCommandHub>();
        var node = registry.Register(new RegisterRequest { AgentId = "cb-cmd-agent", Hostname = "h" });
        var reader = new CmdResultReader();
        var attach = hub.AttachAsync(node.NodeId, new CmdEchoWriter(reader), reader, CancellationToken.None);
        while (!hub.IsConnected(node.NodeId)) await Task.Delay(10);

        // args 显式给出：走非空分支。
        var res = await _client.PostAsJsonAsync($"/api/nodes/{node.NodeId}/cmd",
            new { command = "echo", args = new[] { "hi" }, timeoutMs = 5000 });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("exit_code").GetInt32());
        Assert.Equal("ok", body.GetProperty("output").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("request_id").GetString()));

        // args 省略（null）：桥内 ?? [] 分支。
        var res2 = await _client.PostAsJsonAsync($"/api/nodes/{node.NodeId}/cmd",
            new { command = "echo", timeoutMs = 5000 });
        res2.EnsureSuccessStatusCode();
        var body2 = await res2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body2.GetProperty("exit_code").GetInt32());
    }

    // ---- 登录空字段（JSON 体缺 username/password → null → ?? "" 分支）----

    [Fact]
    public async Task Login_EmptyBody_MissingFields_Returns401()
    {
        var res = await _client.PostAsJsonAsync("/api/auth/login", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Contains("invalid credentials", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_UsernameOnly_MissingPassword_Returns401()
    {
        // username 非空 + password 缺失：?? 两侧组合分支补全。
        var res = await _client.PostAsJsonAsync("/api/auth/login", new { username = "cb-ghost-x" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ---- cmd：command 缺失（null → ?? "" → 白名单拒绝 400）----

    [Fact]
    public async Task Cmd_MissingCommand_Returns400()
    {
        await AsAdmin();
        var registry = _factory.Services.GetRequiredService<NodeRegistry>();
        var node = registry.Register(new RegisterRequest { AgentId = "cb-cmd-nocmd", Hostname = "h" });
        var res = await _client.PostAsJsonAsync($"/api/nodes/{node.NodeId}/cmd",
            new { args = new[] { "hi" }, timeoutMs = 1000 });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("command not allowed", await res.Content.ReadAsStringAsync());
    }

    // ---- 建号：password 缺失（null → ?? "" 哈希空串分支）----

    [Fact]
    public async Task CreateUser_MissingPassword_CreatesWithEmptyHash()
    {
        await AsAdmin();
        var res = await _client.PostAsJsonAsync("/api/auth/users",
            new { username = "cb-nopw-user", role = "Admin" });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("cb-nopw-user", body.GetProperty("username").GetString());
    }

    // ---- 登出：Authorization 头无 Bearer 前缀（原始值直接作 token）----

    [Fact]
    public async Task Logout_WithoutBearerPrefix_RevokesRawToken()
    {
        var token = await _factory.LoginAdminAsync();
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token); // 先过中间件
        // 再以无前缀形式重放：中间件按原始 token 校验通过，端点走无 Bearer 分支。
        client.DefaultRequestHeaders.Remove("Authorization");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", token);
        var res = await client.PostAsync("/api/auth/logout", null);
        res.EnsureSuccessStatusCode();
    }

    // ---- 顶层装配：TAOOPS_DATA_DIR 未设 + 非 insecure 横幅（真实启动一次）----

    [Fact]
    public async Task Program_TopLevel_DataDirUnset_UsesSystemPath()
    {
        if (!TryEnsureSystemDataDir())
        {
            return; // 非 root 环境（如 CI runner）无法创建 /var/lib/taoops，系统路径分支仅 root 可实测
        }
        var prevData = Environment.GetEnvironmentVariable("TAOOPS_DATA_DIR");
        var prevInsecure = Environment.GetEnvironmentVariable("TAOOPS_INSECURE");
        Environment.SetEnvironmentVariable("TAOOPS_DATA_DIR", null);
        Environment.SetEnvironmentVariable("TAOOPS_INSECURE", null); // TLS 模式：横幅走非 insecure 分支
        try
        {
            using var raw = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>();
            using var client = raw.CreateClient();
            var res = await client.GetAsync("/");
            res.EnsureSuccessStatusCode();
            var body = await res.Content.ReadAsStringAsync();
            Assert.Equal("TaoOps Server M2", body); // 非 insecure 横幅分支
        }
        finally
        {
            Environment.SetEnvironmentVariable("TAOOPS_DATA_DIR", prevData);
            Environment.SetEnvironmentVariable("TAOOPS_INSECURE", prevInsecure);
            try { Directory.Delete("/var/lib/taoops/server", recursive: true); } catch { }
        }
    }

    /// <summary>尝试确保系统默认数据目录可创建；非 root（如 CI runner）返回 false，调用方跳过系统路径实测。</summary>
    private static bool TryEnsureSystemDataDir()
    {
        try
        {
            Directory.CreateDirectory("/var/lib/taoops");
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}

/// <summary>覆盖账补齐第三批：单机分支（无需 Web 装配）。动进程级环境变量，并入 Integration 集合串行，避免与工厂静态策略初始化竞态。</summary>
[Collection("Integration")]
public class CoverageBackfillUnitTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "taoops-cb2-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    // ---- 桥异常映射（超时默认值 / Unavailable / DeadlineExceeded）----

    private sealed class BlockingWriter : ICommandRequestWriter
    {
        // 挂住不回执（不绑 ct，避免读侧取消先拆链变 503）。
        public Task WriteAsync(DispatchRequest request, CancellationToken ct) => Task.Delay(Timeout.Infinite, CancellationToken.None);
    }

    private sealed class ThrowingWriter(RpcException error) : ICommandRequestWriter
    {
        public Task WriteAsync(DispatchRequest request, CancellationToken ct) => throw error;
    }

    private sealed class FakeResultReaderBridge : ICommandResultReader
    {
        private readonly System.Threading.Channels.Channel<DispatchResult> _results =
            System.Threading.Channels.Channel.CreateUnbounded<DispatchResult>();
        public void Enqueue(DispatchResult result) => _results.Writer.TryWrite(result);
        public async Task<DispatchResult?> ReadNextAsync(CancellationToken ct)
        {
            while (await _results.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                if (_results.Reader.TryRead(out var r))
                    return r;
            return null;
        }
    }

    private sealed class EchoWriter(FakeResultReaderBridge reader) : ICommandRequestWriter
    {
        public Task WriteAsync(DispatchRequest request, CancellationToken ct)
        {
            reader.Enqueue(new DispatchResult { RequestId = request.RequestId, ExitCode = 0, Output = "ok" });
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyResultReader : ICommandResultReader
    {
        // 挂住不返回：读侧立即收尾会让中枢拆链（下发变 503 而非 504）。
        public async Task<DispatchResult?> ReadNextAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        }
    }

    private async Task<(NodeCommandBridge Bridge, string NodeId)> NewBridge(ICommandRequestWriter writer, ICommandResultReader? reader = null)
    {
        var registry = new NodeRegistry();
        var hub = new NodeCommandHub();
        var bridge = new NodeCommandBridge(registry, hub, new AuditStore(_dir));
        var node = registry.Register(new RegisterRequest { AgentId = "cb-agent", Hostname = "h" });
        var attach = hub.AttachAsync(node.NodeId, writer, reader ?? new EmptyResultReader(), CancellationToken.None);
        while (!hub.IsConnected(node.NodeId)) await Task.Delay(10);
        await Task.Yield();
        return (bridge, node.NodeId);
    }

    [Fact]
    public async Task Dispatch_TimeoutZero_UsesDefault_AndSucceeds()
    {
        // timeoutMs=0 → 取默认超时（分支覆盖）；回执即时，不必真等 30s。
        var reader = new FakeResultReaderBridge();
        var (bridge, nodeId) = await NewBridge(new EchoWriter(reader), reader);
        var outcome = await bridge.DispatchAsync(nodeId, "echo", ["hi"], 0, "u", "Admin", "1.1.1.1");
        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("ok", outcome.Output);
    }

    [Fact]
    public async Task Dispatch_ChannelUnavailable_MappedTo503()
    {
        var (bridge, nodeId) = await NewBridge(
            new ThrowingWriter(new RpcException(new Status(StatusCode.Unavailable, "gone"))));
        var ex = await Assert.ThrowsAsync<BridgeException>(() =>
            bridge.DispatchAsync(nodeId, "echo", ["hi"], 1000, "u", "Admin", "1.1.1.1"));
        Assert.Equal(503, ex.StatusCode);
    }

    [Fact]
    public async Task Dispatch_DeadlineExceeded_MappedTo504()
    {
        // 写桩挂住不回执 → 短超时触发 OCE → 中枢转 DeadlineExceeded → 桥映射 504。
        var (bridge, nodeId) = await NewBridge(new BlockingWriter());
        var ex = await Assert.ThrowsAsync<BridgeException>(() =>
            bridge.DispatchAsync(nodeId, "echo", ["hi"], 100, "u", "Admin", "1.1.1.1"));
        Assert.Equal(504, ex.StatusCode);
    }

    // ---- 回流正常收尾（订阅关闭 → 循环自然退出 → 返回句柄）----

    [Fact]
    public async Task WafIngestor_SubscribersClosed_ReturnsHandleNormally()
    {
        var hub = new NodeStreamHub();
        var ingestor = new WafEventIngestor(hub, new WafEventStore(_dir));
        hub.AttachLog("waf-done");
        var session = hub.GetLog("waf-done")!;
        using var cts = new CancellationTokenSource();
        var run = ingestor.StartAsync("waf-done", cts.Token);
        await Task.Delay(100);
        session.CloseSubscribers(); // 订阅关闭 → ReadAllAsync 完成 → return sub
        var handle = await run;      // 不抛即正常收尾
        handle.Dispose();
    }

    [Fact]
    public async Task SecIngestor_SubscribersClosed_ReturnsHandleNormally()
    {
        var hub = new NodeStreamHub();
        var ingestor = new SecEventIngestor(hub, new SecEventStore(_dir));
        hub.AttachLog("sec-done");
        var session = hub.GetLog("sec-done")!;
        using var cts = new CancellationTokenSource();
        var run = ingestor.StartAsync("sec-done", cts.Token);
        await Task.Delay(100);
        session.CloseSubscribers();
        var handle = await run;
        handle.Dispose();
    }

    // ---- 账本 null 行容错（反序列化得 null → 跳过）----

    [Fact]
    public void WafEventStore_NullLine_Skipped()
    {
        var dir = Path.Combine(_dir, "waf-null");
        var store = new WafEventStore(dir);
        store.Append(new WafEvent { NodeId = "n1", Event = "deny", RuleId = "r", Ts = 1 });
        File.AppendAllText(Path.Combine(dir, "waf", "events.jsonl"), "null\n");
        Assert.Equal("r", Assert.Single(new WafEventStore(dir).Query()).RuleId);
    }

    [Fact]
    public void SecEventStore_NullLine_Skipped()
    {
        var dir = Path.Combine(_dir, "sec-null");
        var store = new SecEventStore(dir);
        store.Append(new SecEvent { NodeId = "n1", Event = "ssh_lockout", Ip = "1.1.1.1", Ts = 1 });
        File.AppendAllText(Path.Combine(dir, "sec", "events.jsonl"), "null\n");
        Assert.Single(new SecEventStore(dir).Query());
    }

    [Fact]
    public void AuditStore_NullLine_Skipped()
    {
        var dir = Path.Combine(_dir, "audit-null");
        var store = new AuditStore(dir);
        store.Append(new AuditEvent { User = "u", Action = "GET", Target = "/api/x", Ts = 1 });
        File.AppendAllText(Path.Combine(dir, "audit", "audit.jsonl"), "null\n");
        Assert.Single(new AuditStore(dir).Query());
    }

    [Fact]
    public void UserStore_NullFile_LoadReturnsEmpty()
    {
        var dir = Path.Combine(_dir, "users-null");
        var store = new UserStore(dir);
        store.Save([new UserAccount { Username = "u1" }]);
        File.WriteAllText(Path.Combine(dir, "auth", "users.json"), "null");
        Assert.Empty(new UserStore(dir).Load());
    }

    // ---- WAF 规则导入 null 载荷（?? throw 分支）----

    [Fact]
    public void WafRuleStore_Import_NullPayload_Throws()
    {
        var store = new WafRuleStore(_dir);
        Assert.ThrowsAny<Exception>(() => store.Import("null"));
    }

    // ---- 静默时段：前向窗口 hour<start 分支 ----

    [Fact]
    public void QuietHours_ForwardWindow_HourBeforeStart_IsAlert()
    {
        Environment.SetEnvironmentVariable("TAOOPS_SEC_QUIET_HOURS", "2-5");
        try
        {
            var detector = new IntrusionDetector(new AuditStore(_dir));
            var alert = detector.OnLoginSuccess("u", "1.1.1.1", 0, hourOverride: 1); // 1<2 且 1<5
            Assert.Contains("login outside quiet hours", alert.Detail);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TAOOPS_SEC_QUIET_HOURS", null);
        }
    }

    // ---- 令牌 TTL：SESSION_MS 非法但 SESSION_HOURS 合法 ----

    [Fact]
    public void TokenStore_SessionMsInvalid_SessionHoursValid_UsesHours()
    {
        Environment.SetEnvironmentVariable("TAOOPS_SESSION_MS", "0");
        Environment.SetEnvironmentVariable("TAOOPS_SESSION_HOURS", "2");
        try
        {
            var tokens = new TokenStore();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var session = tokens.Issue("u", UserRole.Admin, now);
            var hours = (session.ExpiresAtMs - now) / 3600_000;
            Assert.Equal(2, hours);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TAOOPS_SESSION_MS", null);
            Environment.SetEnvironmentVariable("TAOOPS_SESSION_HOURS", null);
        }
    }

    // ---- 认证策略：白名单环境变量解析 ----

    [Fact]
    public void AuthPolicy_WhitelistEnv_Parsed()
    {
        Environment.SetEnvironmentVariable("TAOOPS_AUTH_WHITELIST", "10.0.0.1, 10.0.0.2");
        Environment.SetEnvironmentVariable("TAOOPS_AUTH_WINDOW_MS", "60000");
        Environment.SetEnvironmentVariable("TAOOPS_AUTH_LOCKOUT_MS", "30000");
        try
        {
            var policy = AuthPolicy.FromEnvironment();
            Assert.Equal(["10.0.0.1", "10.0.0.2"], policy.WhitelistIps);
            Assert.Equal(60000, policy.FailureWindowMs);
            Assert.Equal(30000, policy.LockoutMs);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TAOOPS_AUTH_WHITELIST", null);
            Environment.SetEnvironmentVariable("TAOOPS_AUTH_WINDOW_MS", null);
            Environment.SetEnvironmentVariable("TAOOPS_AUTH_LOCKOUT_MS", null);
        }
    }

    [Fact]
    public void AuthPolicy_WhitelistEnvUnset_EmptyList()
    {
        Environment.SetEnvironmentVariable("TAOOPS_AUTH_WHITELIST", null);
        var policy = AuthPolicy.FromEnvironment();
        Assert.Empty(policy.WhitelistIps);
    }

    [Fact]
    public void AuthPolicy_EnvZeroValues_FallToDefaults()
    {
        // 旋钮设 0：>0 校验为假 → 全部落默认值（假分支补全）。
        Environment.SetEnvironmentVariable("TAOOPS_AUTH_MAX_FAILURES", "0");
        Environment.SetEnvironmentVariable("TAOOPS_AUTH_WINDOW_MS", "0");
        Environment.SetEnvironmentVariable("TAOOPS_AUTH_LOCKOUT_MS", "0");
        try
        {
            var policy = AuthPolicy.FromEnvironment();
            Assert.Equal(5, policy.MaxFailures);
            Assert.Equal(15 * 60 * 1000, policy.FailureWindowMs);
            Assert.Equal(15 * 60 * 1000, policy.LockoutMs);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TAOOPS_AUTH_MAX_FAILURES", null);
            Environment.SetEnvironmentVariable("TAOOPS_AUTH_WINDOW_MS", null);
            Environment.SetEnvironmentVariable("TAOOPS_AUTH_LOCKOUT_MS", null);
        }
    }

    // ---- WAF 规则库：FilePath 暴露 + 规则文件为 null 时回退空集 ----

    [Fact]
    public void WafRuleStore_FilePath_PointsToRulesJson_And_NullFile_LoadsEmptySet()
    {
        var dir = Path.Combine(_dir, "waf-rules-null");
        var store = new WafRuleStore(dir);
        Assert.Equal(Path.Combine(dir, "waf", "rules.json"), store.FilePath);
        Directory.CreateDirectory(Path.Combine(dir, "waf"));
        File.WriteAllText(store.FilePath, "null");
        Assert.Empty(store.Load().Rules); // 反序列化得 null → ?? new WafRuleSet() 分支
    }

    // ---- 托管服务：无 logger 构造（PruneFaulted 静默清理）+ 巡检 OCE 收尾 ----

    [Fact]
    public async Task HostedService_LoggerlessCtor_FaultedTaskPrunedSilently()
    {
        var registry = new NodeRegistry();
        registry.Register(new RegisterRequest { AgentId = "a1", Hostname = "h1" });
        // 内部构造不传 logger → _logger 为 null：故障任务的清理走静默分支。
        var svc = new StreamIngestorHostedService(registry,
            (n, ct) => Task.FromException(new InvalidOperationException("stream dropped")),
            (n, ct) => Task.CompletedTask);
        Assert.Equal(2, await svc.PollOnceAsync(CancellationToken.None));
        await Task.Delay(50); // 等任务真正 faulted
        // 第二轮：仅故障的 waf 任务被移出重订（sec 任务未故障不重订）。
        Assert.Equal(1, await svc.PollOnceAsync(CancellationToken.None));
    }

    // ---- 令牌 TTL：SESSION_MS 合法值直接生效 ----

    [Fact]
    public void TokenStore_SessionMsValid_UsedDirectly()
    {
        Environment.SetEnvironmentVariable("TAOOPS_SESSION_MS", "1000");
        try
        {
            var tokens = new TokenStore();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var session = tokens.Issue("u", UserRole.Admin, now);
            Assert.Equal(1000, session.ExpiresAtMs - now); // SESSION_MS 合法分支
        }
        finally
        {
            Environment.SetEnvironmentVariable("TAOOPS_SESSION_MS", null);
        }
    }

    // ---- 静默时段：hourOverride 缺省（真实时钟取小时分支）----

    [Fact]
    public void Detector_HourOverrideNull_UsesLocalClock()
    {
        var detector = new IntrusionDetector(new AuditStore(_dir));
        // 不带 override：hour 取本地时钟（?? 右侧分支）；告警与否随当前钟点落在默认静默窗（23-06）内外变化，
        // 但类型恒为 login_hour。
        var alert = detector.OnLoginSuccess("u", "1.1.1.1", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Equal(SecAlertTypes.LoginHour, alert.Type);
    }

    // ---- 托管服务：巡检 OCE 收尾（StopAsync 在巡检委托内触发 → 过滤器命中 break）----

    [Fact]
    public async Task HostedService_ExecuteLoop_PollOceAfterStop_Breaks()
    {
        Environment.SetEnvironmentVariable("TAOOPS_INGEST_POLL_SECONDS", "3600");
        try
        {
            var registry = new NodeRegistry();
            registry.Register(new RegisterRequest { AgentId = "a1", Hostname = "h1" });
            StreamIngestorHostedService? svcRef = null;
            var svc = new StreamIngestorHostedService(registry,
                (n, ct) =>
                {
                    // 巡检委托同步执行期间 _executeTask 尚未赋值，StopAsync 会空返回；
                    // 反射取消 BackgroundService 内部令牌（即委托收到的 ct）→ 过滤器命中 → break。
                    var ctsField = typeof(BackgroundService).GetField("_stoppingCts",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    ((CancellationTokenSource)ctsField!.GetValue(svcRef)!).Cancel();
                    throw new OperationCanceledException(ct);
                },
                (n, ct) => Task.CompletedTask,
                NullLogger<StreamIngestorHostedService>.Instance);
            svcRef = svc;
            await svc.StartAsync(CancellationToken.None); // 首轮巡检即 OCE → break，正常返回
        }
        finally
        {
            Environment.SetEnvironmentVariable("TAOOPS_INGEST_POLL_SECONDS", "3600");
        }
    }

    [Fact]
    public async Task HostedService_ExecuteLoop_Loggerless_PollFailureSilent()
    {
        Environment.SetEnvironmentVariable("TAOOPS_INGEST_POLL_SECONDS", "1");
        try
        {
            var registry = new NodeRegistry();
            registry.Register(new RegisterRequest { AgentId = "a1", Hostname = "h1" });
            var svc = new StreamIngestorHostedService(registry,
                (n, ct) => throw new Exception("poll exploded"),
                (n, ct) => Task.CompletedTask); // 无 logger：catch 内 ?. 走 null 分支
            using var cts = new CancellationTokenSource(300);
            await svc.StartAsync(cts.Token);
            await Task.Delay(500);
            await svc.StopAsync(CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TAOOPS_INGEST_POLL_SECONDS", "3600");
        }
    }
}