using System.Threading.Channels;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using TaoOps.Server.Alerts;
using TaoOps.Server.Auth;
using TaoOps.Server.Audit;
using TaoOps.Server.Grpc;
using TaoOps.Server.Push;
using TaoOps.Server.Relay;
using TaoOps.Server.Services;
using TaoOps.Server.Services.K8s;
using TaoOps.Server.Settings;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>
/// 分支账批次四第十波（清尾）：各 Policy 未知角色拒绝、存储类空文件/损坏文件兜底、
/// 参数构造全字段、建议数据源派发异常链、推送默认客户端、监控宿主异常韧性、
/// 中转告警/审计可选依赖 null 臂、批量通道取消与空参数。
/// </summary>
public class Batch4Wave10Tests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"taoops-w10-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private string SubDir(string name)
    {
        var dir = Path.Combine(_dataDir, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---------- 1. 各 Policy：未知角色一律拒绝（TryGetValue false 臂） ----------

    [Fact]
    public void Policies_UnknownRole_IsDenied()
    {
        var unknown = (UserRole)999;
        Assert.False(CronPolicy.Allows("list", unknown));
        Assert.False(DbPolicy.Allows("list", unknown));
        Assert.False(DockerPolicy.Allows("list", unknown));
        Assert.False(EnvPolicy.Allows("list", unknown));
        Assert.False(FilesPolicy.Allows("list", unknown));
        Assert.False(FtpPolicy.Allows("list", unknown));
        Assert.False(FwPolicy.Allows("list", unknown));
        Assert.False(TamperGuardPolicy.Allows("list", unknown));
        Assert.False(SitePolicy.Allows("list", unknown));
        Assert.False(WebStatsPolicy.Allows("list", unknown));
        Assert.False(SslPolicy.Allows("list", unknown));
        Assert.False(AppDeployPolicy.Allows("list", unknown));
        Assert.False(BackupPolicyOps.Allows("list", unknown));
        Assert.False(K8sPolicy.Allows("nodes", unknown));
    }

    // ---------- 2. SettingsStore：文件内容为 null 字面量 → 回退默认 ----------

    [Fact]
    public void SettingsStore_FileNullLiteral_ReturnsDefaults()
    {
        var store = new SettingsStore(SubDir("settings"));
        File.WriteAllText(store.FilePath, "null");
        var settings = store.Load();
        Assert.NotNull(settings);
        Assert.Equal(90, settings.DiskCriticalPct);
        Assert.Equal(85, settings.DiskResetPct);
    }

    // ---------- 3. MetricsStore.Clear：存在/不存在两臂 ----------

    [Fact]
    public void MetricsStore_Clear_RemovesOnlyTargetNode()
    {
        var store = new MetricsStore();
        store.Add("n1", new MetricsStore.MetricPoint(1, 10, 100, 50, 0, 0, 0, 0));
        store.Add("n2", new MetricsStore.MetricPoint(2, 20, 100, 60, 0, 0, 0, 0));

        store.Clear("n1");
        Assert.Empty(store.Query("n1"));
        Assert.Single(store.Query("n2"));

        store.Clear("ghost"); // 不存在节点：TryRemove false 臂，不抛
        Assert.Single(store.Query("n2"));
    }

    // ---------- 4. PushStores：载入三态（合法 JSON / null 字面量 / 损坏文件） ----------

    [Fact]
    public void PushChannelStore_FileVariants_LoadOrFallback()
    {
        var dir = SubDir("push-ok");
        Directory.CreateDirectory(Path.Combine(dir, "push"));
        File.WriteAllText(Path.Combine(dir, "push", "channels.json"),
            """[{"Id":"a","Name":"n","Url":"http://x.example/h","Enabled":true,"CreatedMs":1}]""");
        Assert.Single(new PushChannelStore(dir).List());

        var dirNull = SubDir("push-null");
        Directory.CreateDirectory(Path.Combine(dirNull, "push"));
        File.WriteAllText(Path.Combine(dirNull, "push", "channels.json"), "null");
        Assert.Empty(new PushChannelStore(dirNull).List());

        var dirBad = SubDir("push-bad");
        Directory.CreateDirectory(Path.Combine(dirBad, "push"));
        File.WriteAllText(Path.Combine(dirBad, "push", "channels.json"), "{oops");
        Assert.Empty(new PushChannelStore(dirBad).List());
    }

    [Fact]
    public void SsrfWhitelistStore_FileVariants_LoadOrFallback()
    {
        var dir = SubDir("ssrf-ok");
        Directory.CreateDirectory(Path.Combine(dir, "push"));
        File.WriteAllText(Path.Combine(dir, "push", "ssrf-whitelist.json"),
            """["internal.example","10.0.0.0/8"]""");
        Assert.Equal(2, new SsrfWhitelistStore(dir).List().Count);

        var dirNull = SubDir("ssrf-null");
        Directory.CreateDirectory(Path.Combine(dirNull, "push"));
        File.WriteAllText(Path.Combine(dirNull, "push", "ssrf-whitelist.json"), "null");
        Assert.Empty(new SsrfWhitelistStore(dirNull).List());

        var dirBad = SubDir("ssrf-bad");
        Directory.CreateDirectory(Path.Combine(dirBad, "push"));
        File.WriteAllText(Path.Combine(dirBad, "push", "ssrf-whitelist.json"), "[oops");
        Assert.Empty(new SsrfWhitelistStore(dirBad).List());
    }

    // ---------- 5. AlertLevels：IsValid 与 All 初始化 ----------

    [Fact]
    public void AlertLevels_IsValid_CoversTableAndPredicate()
    {
        Assert.True(AlertLevels.IsValid("info"));
        Assert.True(AlertLevels.IsValid("warn"));
        Assert.True(AlertLevels.IsValid("critical"));
        Assert.False(AlertLevels.IsValid("nope"));
        Assert.Equal(3, AlertLevels.All.Count);
    }

    // ---------- 6. AlertStore 构造：坏行跳过 + 过期条目启动即清 ----------

    [Fact]
    public void AlertStore_Ctor_SkipsBadLineAndPrunesExpired()
    {
        var dir = SubDir("alerts");
        Directory.CreateDirectory(Path.Combine(dir, "alerts"));
        var path = Path.Combine(dir, "alerts", "alerts.jsonl");
        var expired = """{"Id":"old","Source":"disk","Level":"warn","NodeId":"n1","Detail":"d","Ts":1000}""";
        var fresh = "{\"Id\":\"new\",\"Source\":\"disk\",\"Level\":\"warn\",\"NodeId\":\"n1\",\"Detail\":\"d\",\"Ts\":"
            + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}";
        File.WriteAllLines(path, [expired, "{oops", fresh]);

        var store = new AlertStore(dir);
        Assert.Single(store.Query()); // 坏行被跳过，过期条目被启动清理
        Assert.Equal("new", store.Query()[0].Id);
    }

    // ---------- 7. UserStore：改密用户不存在返回 false ----------

    [Fact]
    public void UserStore_UpdatePasswordHash_UnknownUser_ReturnsFalse()
    {
        var store = new UserStore(SubDir("users"));
        Assert.False(store.UpdatePasswordHash("ghost", "hash-x"));
    }

    // ---------- 8. 参数构造：全字段摊平 ----------

    [Fact]
    public void SslOpArgs_AllFieldsSet_EmitsEveryKey()
    {
        var args = SslOpArgs.Build(new SslOpRequest("issue", "a.example", "a@example.com", "/var/www",
            "-----BEGIN CERT-----", "-----KEY-----", 90, 5000));
        Assert.Contains("op=issue", args);
        Assert.Contains("domain=a.example", args);
        Assert.Contains("email=a@example.com", args);
        Assert.Contains("webroot=/var/www", args);
        Assert.Contains("cert_pem=-----BEGIN CERT-----", args);
        Assert.Contains("key_pem=-----KEY-----", args);
        Assert.Contains("days=90", args);
    }

    [Fact]
    public void BackupPolicyArgs_AllFieldsSet_EmitsEveryKey()
    {
        var args = BackupPolicyArgs.Build(new BackupPolicyRequest("apply", "id1", "nightly", "/data",
            "full", "daily:02:30", 7, 5000));
        Assert.Contains("op=apply", args);
        Assert.Contains("id=id1", args);
        Assert.Contains("name=nightly", args);
        Assert.Contains("source=/data", args);
        Assert.Contains("type=full", args);
        Assert.Contains("schedule=daily:02:30", args);
        Assert.Contains("retain=7", args);
    }

    // ---------- 9. K8sRequest：全字段构造（record 属性初始化行） ----------

    [Fact]
    public void K8sRequest_FullConstruction_PreservesFields()
    {
        var req = new K8sRequest("deploy", "wp", "kubeconfig", "ns1", "deploy1", "3",
            "ns1", "pod1", "c1", "wordpress", Confirm: true, TimeoutMs: 8000);
        Assert.True(req.Confirm);
        Assert.Equal("deploy1", req.Deployment);
        Assert.Equal("3", req.Replicas);
    }

    // ---------- 10. 建议数据源：派发失败/空结果/合法输出 ----------

    [Fact]
    public async Task AdviceDataSource_DispatchNullOutcome_ReturnsInsufficientData()
    {
        var ds = NewDataSource((_, _, _) => Task.FromResult<NodeCommandBridge.CommandOutcome>(null!));
        Assert.Empty(ds.GetCertStatus("n1"));
        Assert.Equal(-1, ds.GetBackupCoverage("n1"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AdviceDataSource_DispatchNonZeroExit_ReturnsInsufficientData()
    {
        var ds = NewDataSource((_, _, _) => Task.FromResult(new NodeCommandBridge.CommandOutcome(1, "err", "r1")));
        Assert.Empty(ds.GetCertStatus("n1"));
        Assert.Equal(-1, ds.GetBackupCoverage("n1"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AdviceDataSource_ValidSslOutput_ParsesCerts()
    {
        var ds = NewDataSource((_, _, _) => Task.FromResult(new NodeCommandBridge.CommandOutcome(0,
            """[{"domain":"a.example","not_after":"2030-01-01T00:00:00+00:00"},{"domain":"b.example","not_after":"not-a-date"}]""",
            "r1")));
        var certs = ds.GetCertStatus("n1");
        Assert.Equal(2, certs.Count);
        Assert.True(certs[0].ExpiresAt > DateTimeOffset.MinValue);
        Assert.Equal(DateTimeOffset.MinValue, certs[1].ExpiresAt); // 非法日期回退
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AdviceDataSource_ValidBackupOutput_CountsEnabled()
    {
        var ds = NewDataSource((_, _, _) => Task.FromResult(new NodeCommandBridge.CommandOutcome(0,
            """[{"enabled":true},{"enabled":false},{"enabled":true}]""", "r1")));
        Assert.Equal(2, ds.GetBackupCoverage("n1"));
        await Task.CompletedTask;
    }

    private DefaultAdviceDataSource NewDataSource(
        Func<string, string[], int, Task<NodeCommandBridge.CommandOutcome>> dispatch)
    {
        var metrics = new MetricsStore();
        var alerts = new AlertStore(SubDir("ds-alerts"));
        var bridge = new NodeCommandBridge(new NodeRegistry(), new NodeCommandHub(), new AuditStore(SubDir("ds-audit")));
        return new DefaultAdviceDataSource(metrics, alerts, bridge, dispatch);
    }

    // ---------- 11. SsrfGuard：空主机名 + DNS 解析抛错 ----------

    [Fact]
    public void SsrfGuard_EmptyHost_Rejected()
    {
        Assert.Equal("URL must be absolute http(s)", SsrfGuard.ValidateUrl("http:///path"));
    }

    [Fact]
    public async Task SsrfGuard_ResolveThrows_ReportsDnsFailure()
    {
        var error = await SsrfGuard.ValidateTargetAsync("http://boom.example/hook", [],
            resolve: _ => throw new InvalidOperationException("dns down"));
        Assert.Equal("DNS resolution failed for boom.example", error);
    }

    // ---------- 12. 批量通道：空参数校验 + 取消令牌 ----------

    [Fact]
    public void BatchService_ValidateOp_EmptyArgs_ThrowsOpNotAllowed()
    {
        var ex = Assert.Throws<BatchService.BatchException>(() => BatchService.ValidateOp("docker.manage", []));
        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("op not allowed", ex.Message);
    }

    [Fact]
    public async Task BatchService_RunAsync_CancelledToken_MarksTimeoutWithoutThrowing()
    {
        var dir = SubDir("batch");
        var audit = new AuditStore(dir);
        var registry = new NodeRegistry();
        var hub = new NodeCommandHub();
        var batch = new BatchService(new NodeCommandBridge(registry, hub, audit), audit);

        var node = registry.Register(new RegisterRequest { AgentId = "b1", Hostname = "b1" });
        var reader = new NeverResultReader();
        _ = hub.AttachAsync(node.NodeId, new SilentRequestWriter(), reader, CancellationToken.None);
        while (!hub.IsConnected(node.NodeId)) await Task.Delay(10);

        using var cts = new CancellationTokenSource(80); // 派发挂起后超时取消
        var result = await batch.RunAsync("sysinfo.overview", [], [node.NodeId], "u", "Admin", "127.0.0.1", cts.Token);
        Assert.Single(result.Nodes);
        Assert.Equal("timeout", result.Nodes[0].Status); // OCE → batch cancelled
    }

    private sealed class SilentRequestWriter : ICommandRequestWriter
    {
        public Task WriteAsync(DispatchRequest request, CancellationToken ct) => Task.CompletedTask; // 不回执，派发挂起
    }

    private sealed class NeverResultReader : ICommandResultReader
    {
        public async Task<DispatchResult?> ReadNextAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        }
    }

    // ---------- 13. 中转：可选告警/审计依赖为 null 的臂 ----------

    [Fact]
    public async Task RelayHub_WithoutAlertCenter_TargetNotConnected_StillNacks()
    {
        var registry = new NodeRegistry();
        var hub = new NodeCommandHub();
        var relay = new RelayHub(hub, alerts: null, audit: new AuditStore(SubDir("relay-a")));
        var acks = await RunRelayAsync(relay, Wave10Chunk(0));
        Assert.Single(acks);
        Assert.False(acks[0].Ok); // 告警中心缺位不抛，NACK 照常
    }

    [Fact]
    public async Task RelayHub_WithoutAudit_HappyPath_StillAcked()
    {
        var registry = new NodeRegistry();
        var hub = new NodeCommandHub();
        var relay = new RelayHub(hub, alerts: null, audit: null); // 审计缺位：finally _audit?.Append null 臂
        var reader = new Wave10ResultReader();
        _ = hub.AttachAsync("node-t", new Wave10ScriptedWriter(reader), reader, CancellationToken.None);
        while (!hub.IsConnected("node-t")) await Task.Delay(10);

        var acks = await RunRelayAsync(relay, Wave10Chunk(0, data: [1, 2, 3]), Wave10Chunk(1, eof: true));
        Assert.Equal(2, acks.Count);
        Assert.All(acks, a => Assert.True(a.Ok));
    }

    private static RelayChunk Wave10Chunk(int index, bool eof = false, byte[]? data = null) => new()
    {
        RelayId = "rl-w10",
        TargetNodeId = "node-t",
        UploadId = "up-w10",
        Name = "site-a",
        ChunkIndex = index,
        TotalSize = 10,
        ChunkCount = 2,
        FileSha256 = "abc",
        ChunkSha256 = "def",
        Data = Google.Protobuf.ByteString.CopyFrom(data ?? []),
        Eof = eof,
        Source = "/var/www/a",
    };

    private static async Task<List<RelayAck>> RunRelayAsync(RelayHub relay, params RelayChunk[] chunks)
    {
        var acks = new Wave10AckWriter();
        await relay.HandleStream(new Wave10ChunkStreamReader(chunks), acks, new Wave10CallContext());
        return acks.Written;
    }

    private sealed class Wave10ChunkStreamReader(params RelayChunk[] items) : IAsyncStreamReader<RelayChunk>
    {
        private int _pos;
        public Task<bool> MoveNext(CancellationToken ct) => Task.FromResult(_pos < items.Length);
        public RelayChunk Current => items[_pos++];
    }

    private sealed class Wave10AckWriter : IServerStreamWriter<RelayAck>
    {
        public List<RelayAck> Written { get; } = [];
        public Task WriteAsync(RelayAck message) { Written.Add(message); return Task.CompletedTask; }
        public Task WriteAsync(RelayAck message, CancellationToken ct) { Written.Add(message); return Task.CompletedTask; }
        public WriteOptions? WriteOptions { get; set; }
    }

    private sealed class Wave10ResultReader : ICommandResultReader
    {
        private readonly Channel<DispatchResult> _results = Channel.CreateUnbounded<DispatchResult>();
        public void Enqueue(DispatchResult result) => _results.Writer.TryWrite(result);

        public async Task<DispatchResult?> ReadNextAsync(CancellationToken ct)
        {
            while (await _results.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                if (_results.Reader.TryRead(out var r))
                    return r;
            return null;
        }
    }

    private sealed class Wave10ScriptedWriter(Wave10ResultReader reader) : ICommandRequestWriter
    {
        public Task WriteAsync(DispatchRequest request, CancellationToken ct)
        {
            reader.Enqueue(new DispatchResult { RequestId = request.RequestId, ExitCode = 0, Output = "ok" });
            return Task.CompletedTask;
        }
    }

    private sealed class Wave10CallContext : ServerCallContext
    {
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
        protected override Metadata RequestHeadersCore => new();
        protected override Metadata ResponseTrailersCore => new();
        protected override Status StatusCore { get; set; } = Status.DefaultSuccess;
        protected override string MethodCore => "/taoops.v1.AgentService/BackupRelay";
        protected override string PeerCore => "ipv4:127.0.0.1:1";
        protected override AuthContext AuthContextCore => new("", new Dictionary<string, List<AuthProperty>>());
        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            throw new NotImplementedException();
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override string HostCore => "";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
    }

    // ---------- 14. 推送：默认客户端（禁重定向）+ 连接失败重试留痕 ----------

    [Fact]
    public async Task PushDispatcher_DefaultClient_ConnectionRefused_RetriesAndLogs()
    {
        var dir = SubDir("push-dispatch");
        var channels = new PushChannelStore(dir);
        var whitelist = new SsrfWhitelistStore(dir);
        var log = new PushLogStore(dir);
        var dispatcher = new PushDispatcher(channels, whitelist, log,
            delay: () => Task.CompletedTask); // 不注 clientFactory → 走 NewClient（禁自动重定向）
        channels.Upsert(new PushChannel { Name = "hook", Url = "http://127.0.0.1:1/x" });

        var results = await dispatcher.SendAsync(new Alert
        {
            Id = "a1", Source = "disk", Level = "critical", NodeId = "n1",
            Detail = "disk 95%", Ts = 1700000000000,
        });
        Assert.Single(results);
        Assert.False(results[0].Ok);
        Assert.Equal(4, results[0].Attempts); // 首次 + 3 次重试
        Assert.NotEmpty(results[0].Error);
        Assert.Equal(4, log.Query().Count);   // 每次尝试独立留痕
    }

    // ---------- 15. 监控宿主：异常韧性（catch 记日志续跑）与取消传播 ----------

    [Fact]
    public async Task K8sMonitor_ExecuteAsync_CancelDuringDelay_ExitsCleanly()
    {
        // 取消落在 Task.Delay：TaskCanceledException 被 catch(Exception) 吞掉记日志，
        // 循环条件转 false 正常退出（PeriodicTimer 系监控则由 WaitForNextTick 抛 OCE 直退）。
        var monitor = new Services.K8s.K8sMonitor(
            new KubeClusterStore(SubDir("k8smon-exit")), new K8sClientFactory(),
            NewAlertCenter("k8smon-exit"), NullLogger<Services.K8s.K8sMonitor>.Instance);
        using var cts = new CancellationTokenSource();
        await monitor.StartAsync(cts.Token);
        var task = monitor.ExecuteTask!;
        await cts.CancelAsync();
        var finished = await Task.WhenAny(task, Task.Delay(3000));
        Assert.True(finished == task, "monitor did not exit after cancel");
        Assert.False(task.IsFaulted, "cancel during delay must not fault the monitor");
    }

    [Fact]
    public async Task K8sMonitor_ExecuteAsync_PreCanceledToken_ExitsImmediately()
    {
        // 令牌先于启动取消：循环条件首查即 false，方法体直接走完（false 臂）。
        var monitor = new Services.K8s.K8sMonitor(
            new KubeClusterStore(SubDir("k8smon-pre")), new K8sClientFactory(),
            NewAlertCenter("k8smon-pre"), NullLogger<Services.K8s.K8sMonitor>.Instance);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await monitor.StartAsync(cts.Token);
        var task = monitor.ExecuteTask!;
        var finished = await Task.WhenAny(task, Task.Delay(3000));
        Assert.True(finished == task, "monitor did not exit with pre-canceled token");
    }

    [Fact]
    public async Task SsrfGuard_ValidateTargetAsync_ShapeError_ShortCircuits()
    {
        // URL 形态不合法：不做 DNS 解析直接短路返回形态错误。
        var err = await SsrfGuard.ValidateTargetAsync("ftp://x.example", [],
            _ => Task.FromResult(Array.Empty<System.Net.IPAddress>()));
        Assert.Equal("URL must be absolute http(s)", err);
    }

    [Fact]
    public async Task BackupScheduleMonitor_ExecuteAsync_SwallowsPollErrors_UntilCancel()
    {
        var monitor = NewBackupMonitor((_, _, _) => Task.FromException<NodeCommandBridge.CommandOutcome>(new Exception("boom")));
        await RunUntilCancel(monitor);
    }

    [Fact]
    public async Task BackupScheduleMonitor_ExecuteAsync_OceFromPoll_Propagates()
    {
        var monitor = NewBackupMonitor((_, _, _) => Task.FromException<NodeCommandBridge.CommandOutcome>(new OperationCanceledException()),
            nodes: () => [("n1", true)]);
        await RunUntilCancelExpectCancel(monitor);
    }

    [Fact]
    public async Task MetricsSampler_ExecuteAsync_SwallowsPollErrors_UntilCancel()
    {
        var sampler = new MetricsSampler(
            bridge: new NodeCommandBridge(new NodeRegistry(), new NodeCommandHub(), new AuditStore(SubDir("ms-mon"))),
            nodes: () => [],
            store: new MetricsStore(),
            logger: NullLogger<MetricsSampler>.Instance,
            interval: TimeSpan.FromMilliseconds(10),
            dispatch: (_, _, _) => Task.FromException<NodeCommandBridge.CommandOutcome>(new Exception("boom")));
        await RunUntilCancel(sampler);
    }

    [Fact]
    public async Task SslMonitor_ExecuteAsync_SwallowsPollErrors_UntilCancel()
    {
        var monitor = NewSslMonitor((_, _, _) => Task.FromException<NodeCommandBridge.CommandOutcome>(new Exception("boom")));
        await RunUntilCancel(monitor);
    }

    [Fact]
    public async Task ReplicationMonitor_ExecuteAsync_SwallowsPollErrors_UntilCancel()
    {
        var monitor = NewReplicationMonitor((_, _, _) => Task.FromException<NodeCommandBridge.CommandOutcome>(new Exception("boom")));
        await RunUntilCancel(monitor);
    }

    [Fact]
    public async Task ReplicationMonitor_PollOnce_BadJson_SkipsWithoutAlert()
    {
        var store = new AlertStore(SubDir("repl-badjson"));
        var monitor = new ReplicationMonitor(
            bridge: new NodeCommandBridge(new NodeRegistry(), new NodeCommandHub(), new AuditStore(SubDir("repl-badjson-audit"))),
            nodes: () => [("n1", true)],
            alerts: new AlertCenter(store),
            logger: NullLogger<ReplicationMonitor>.Instance,
            interval: TimeSpan.FromMilliseconds(10),
            dispatch: (_, _, _) => Task.FromResult(new NodeCommandBridge.CommandOutcome(0, "{not-json", "r1")));
        await monitor.PollOnce();
        Assert.Empty(store.Query()); // 坏 JSON 整轮跳过，不告警不抛错
    }

    [Fact]
    public async Task ReplicationMonitor_PollOnce_LagAlert_EmptyLastError_NoSuffix()
    {
        var store = new AlertStore(SubDir("repl-lag"));
        var monitor = new ReplicationMonitor(
            bridge: new NodeCommandBridge(new NodeRegistry(), new NodeCommandHub(), new AuditStore(SubDir("repl-lag-audit"))),
            nodes: () => [("n1", true)],
            alerts: new AlertCenter(store),
            logger: NullLogger<ReplicationMonitor>.Instance,
            interval: TimeSpan.FromMilliseconds(10),
            dispatch: (_, _, _) => Task.FromResult(new NodeCommandBridge.CommandOutcome(0,
                "{\"configured\":true,\"io_running\":\"Yes\",\"sql_running\":\"Yes\",\"seconds_behind_master\":9999,\"last_error\":\"\"}", "r1")));
        await monitor.PollOnce();
        var alert = Assert.Single(store.Query());
        Assert.Contains("replica lag", alert.Detail);
        Assert.DoesNotContain("last_error", alert.Detail); // last_error 为空 → 无后缀臂
    }

    [Fact]
    public async Task ReplicationMonitor_PollOnce_ThreadDown_WithLastError_AppendsSuffix()
    {
        var store = new AlertStore(SubDir("repl-lag2"));
        var monitor = new ReplicationMonitor(
            bridge: new NodeCommandBridge(new NodeRegistry(), new NodeCommandHub(), new AuditStore(SubDir("repl-lag2-audit"))),
            nodes: () => [("n1", true)],
            alerts: new AlertCenter(store),
            logger: NullLogger<ReplicationMonitor>.Instance,
            interval: TimeSpan.FromMilliseconds(10),
            dispatch: (_, _, _) => Task.FromResult(new NodeCommandBridge.CommandOutcome(0,
                "{\"configured\":true,\"io_running\":\"No\",\"sql_running\":\"Yes\",\"seconds_behind_master\":10,\"last_error\":\"io hung\"}", "r1")));
        await monitor.PollOnce();
        var alert = Assert.Single(store.Query());
        Assert.Contains("replication thread down", alert.Detail);
        Assert.Contains("last_error=io hung", alert.Detail); // last_error 非空 → 带后缀臂
    }

    [Fact]
    public async Task NodeCommandBridge_DispatchAsync_TimeOutOfRange_Throws()
    {
        var registry = new NodeRegistry();
        var node = registry.Register(new RegisterRequest { AgentId = "a1", Hostname = "h1" });
        var bridge = new NodeCommandBridge(registry, new NodeCommandHub(), new AuditStore(SubDir("bridge-timeout")));
        var ex = await Assert.ThrowsAsync<BridgeException>(
            () => bridge.DispatchAsync(node.NodeId, "uname", [], 300_001, "u", "Admin", ""));
        Assert.Contains("out of range", ex.Message); // timeout > MaxTimeoutMs 臂
    }

    [Fact]
    public async Task NodeOfflineMonitor_ExecuteAsync_SwallowsPollErrors_UntilCancel()
    {
        var monitor = new NodeOfflineMonitor(
            nodes: () => throw new InvalidOperationException("registry boom"),
            alerts: NewAlertCenter("offline"),
            logger: NullLogger<NodeOfflineMonitor>.Instance,
            interval: TimeSpan.FromMilliseconds(10));
        await RunUntilCancel(monitor);
    }

    [Fact]
    public async Task NodeOfflineMonitor_ExecuteAsync_OceFromPoll_Propagates()
    {
        var monitor = new NodeOfflineMonitor(
            nodes: () => throw new OperationCanceledException(),
            alerts: NewAlertCenter("offline-oce"),
            logger: NullLogger<NodeOfflineMonitor>.Instance,
            interval: TimeSpan.FromMilliseconds(10));
        await RunUntilCancelExpectCancel(monitor);
    }

    private BackupScheduleMonitor NewBackupMonitor(
        Func<string, string[], int, Task<NodeCommandBridge.CommandOutcome>> dispatch,
        Func<IEnumerable<(string NodeId, bool Connected)>>? nodes = null)
    {
        var dir = SubDir("bsm-" + Guid.NewGuid().ToString("N")[..8]);
        return new BackupScheduleMonitor(
            bridge: new NodeCommandBridge(new NodeRegistry(), new NodeCommandHub(), new AuditStore(dir)),
            nodes: nodes ?? (() => []),
            alerts: NewAlertCenter("bsm"),
            logger: NullLogger<BackupScheduleMonitor>.Instance,
            interval: TimeSpan.FromMilliseconds(10),
            dispatch: dispatch);
    }

    private SslMonitor NewSslMonitor(
        Func<string, string[], int, Task<NodeCommandBridge.CommandOutcome>> dispatch)
    {
        return new SslMonitor(
            bridge: new NodeCommandBridge(new NodeRegistry(), new NodeCommandHub(), new AuditStore(SubDir("ssl-mon"))),
            nodes: () => [],
            alerts: NewAlertCenter("ssl"),
            logger: NullLogger<SslMonitor>.Instance,
            interval: TimeSpan.FromMilliseconds(10),
            dispatch: dispatch);
    }

    private ReplicationMonitor NewReplicationMonitor(
        Func<string, string, int, Task<NodeCommandBridge.CommandOutcome>> dispatch)
    {
        return new ReplicationMonitor(
            bridge: new NodeCommandBridge(new NodeRegistry(), new NodeCommandHub(), new AuditStore(SubDir("repl-mon"))),
            nodes: () => [],
            alerts: NewAlertCenter("repl"),
            logger: NullLogger<ReplicationMonitor>.Instance,
            interval: TimeSpan.FromMilliseconds(10),
            dispatch: dispatch);
    }

    private AlertCenter NewAlertCenter(string name) =>
        new(new AlertStore(SubDir("ac-" + name)));

    /// <summary>起跑后台巡检：异常轮次被吞掉继续跑，取消后任务以取消态收束。</summary>
    private static async Task RunUntilCancel(BackgroundService monitor)
    {
        using var cts = new CancellationTokenSource();
        await monitor.StartAsync(cts.Token); // BackgroundService.StartAsync 返回 CompletedTask，真任务在 ExecuteTask
        var task = monitor.ExecuteTask!;
        await Task.Delay(120); // ≥10 个周期：异常轮次被吞掉，循环未溃
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    /// <summary>巡检体抛 OCE：rethrow 臂生效，任务直接以取消态收束。</summary>
    private static async Task RunUntilCancelExpectCancel(BackgroundService monitor)
    {
        using var cts = new CancellationTokenSource();
        await monitor.StartAsync(cts.Token);
        var task = monitor.ExecuteTask!;
        var finished = await Task.WhenAny(task, Task.Delay(3000));
        Assert.True(finished == task, "monitor did not stop after OCE from poll");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    // ---------- 16. 阈值 env 解析全臂 + 到期判定边界 ----------

    [Fact]
    public void OfflineThreshold_EnvVariants_FallBackOrApply()
    {
        Assert.Equal(300, WithEnv("TAOOPS_NODE_OFFLINE_THRESHOLD", "abc", () => NodeOfflineMonitor.OfflineThresholdSec));
        Assert.Equal(300, WithEnv("TAOOPS_NODE_OFFLINE_THRESHOLD", "-5", () => NodeOfflineMonitor.OfflineThresholdSec));
        Assert.Equal(45, WithEnv("TAOOPS_NODE_OFFLINE_THRESHOLD", "45", () => NodeOfflineMonitor.OfflineThresholdSec));
    }

    [Fact]
    public void LagThreshold_EnvVariants_FallBackOrApply()
    {
        Assert.Equal(300, WithEnv("TAOOPS_REPLICA_LAG_THRESHOLD", "abc", () => ReplicationMonitor.LagThresholdSec));
        Assert.Equal(300, WithEnv("TAOOPS_REPLICA_LAG_THRESHOLD", "-5", () => ReplicationMonitor.LagThresholdSec));
        Assert.Equal(60, WithEnv("TAOOPS_REPLICA_LAG_THRESHOLD", "60", () => ReplicationMonitor.LagThresholdSec));
    }

    [Fact]
    public async Task BackupScheduleMonitor_PollOnce_ShortOrBadInterval_SkipsWithoutRun()
    {
        var calls = new List<string[]>();
        var dir = SubDir("bsm-due");
        var alerts = new AlertStore(dir);
        var monitor = new BackupScheduleMonitor(
            bridge: new NodeCommandBridge(new NodeRegistry(), new NodeCommandHub(), new AuditStore(SubDir("bsm-due-audit"))),
            nodes: () => [("n1", true)],
            alerts: new AlertCenter(alerts),
            logger: NullLogger<BackupScheduleMonitor>.Instance,
            dispatch: (nodeId, args, timeout) =>
            {
                calls.Add(args);
                return Task.FromResult(new NodeCommandBridge.CommandOutcome(0,
                    "[{\"id\":\"p1\",\"name\":\"nightly\",\"enabled\":true,\"schedule\":\"interval:30\",\"last_run_ms\":0}," +
                    "{\"id\":\"p2\",\"name\":\"broken\",\"enabled\":true,\"schedule\":\"interval:abc\",\"last_run_ms\":0}]",
                    "r1"));
            });

        await monitor.PollOnce();
        // 周期 <60s 与非法周期均不到期：不触发执行下发，也不告警。
        Assert.Single(calls); // 仅 policy_list，无 policy_run
        Assert.Empty(alerts.Query());
    }

    private static T WithEnv<T>(string name, string value, Func<T> probe)
    {
        var old = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, value);
            return probe();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, old);
        }
    }
}
