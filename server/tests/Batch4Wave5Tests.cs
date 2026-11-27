using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using k8s.Models;
using TaoOps.Server.Alerts;
using TaoOps.Server.Audit;
using TaoOps.Server.Auth;
using TaoOps.Server.Services.K8s;

namespace TaoOps.Server.Tests;

/// <summary>
/// 分支账批次四第五波：TOTP 边界与重放臂、TotpService 用户缺失/空码/未启用臂、
/// K8sMonitor 判定缺省臂与巡检复位跨集群环、K8sClusterService 命名空间列表/
/// 最小事件映射/重启激增健康臂/指标缺 metadata 与非法后缀臂。
/// </summary>
public class Batch4Wave5TotpTests
{
    private static (UserStore Users, AuthService Auth, TotpStore Store, TotpService Svc, string DataDir) NewEnv()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"totp5-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        var users = new UserStore(dataDir);
        users.Add(new UserAccount
        {
            Username = "alice",
            PasswordHash = AuthService.HashPassword("correct-horse"),
            Role = UserRole.Admin,
            CreatedAtMs = 0,
        });
        var audit = new AuditStore(dataDir);
        var policy = new AuthPolicy { MaxFailures = 5, FailureWindowMs = 15 * 60 * 1000, LockoutMs = 5 * 60 * 1000 };
        var auth = new AuthService(users, new TokenStore(), audit, policy);
        var store = new TotpStore(dataDir);
        var svc = new TotpService(store, users, auth, new TokenStore(), audit);
        return (users, auth, store, svc, dataDir);
    }

    private static string ComputeCode(string secret, long step)
    {
        var key = Totp.Base32Decode(secret)!;
        var counter = new byte[8];
        var s = step;
        for (var i = 7; i >= 0; i--) { counter[i] = (byte)(s & 0xFF); s >>= 8; }
        using var hmac = new System.Security.Cryptography.HMACSHA1(key);
        var hash = hmac.ComputeHash(counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    // ---- Totp 纯函数：Base32 余数臂、空串解码、空码/重放/非法密钥臂 ----

    [Fact]
    public void Base32_EncodeRemainder_And_DecodeEmpty()
    {
        // 1 字节 = 8 位：整块 1 字符 + 余数臂（bitCount=3>0）→ 共 2 字符。
        var one = Totp.Base32Encode(new byte[1]);
        Assert.Equal(2, one.Length);
        // 5 字节 = 40 位：恰好 8 字符，余数臂 false。
        var five = Totp.Base32Encode(new byte[5]);
        Assert.Equal(8, five.Length);
        Assert.Equal(one, Totp.Base32Encode(Totp.Base32Decode(one)!)); // 往返一致
        Assert.Null(Totp.Base32Decode("")); // 空串 → null 臂
    }

    [Fact]
    public void Verify_NullCode_Replay_And_MatchStep_Arms()
    {
        var secret = Totp.GenerateSecret();
        var now = 1_700_000_000L;
        var step = now / 30;

        // code null → false / -1（?.Length 短路臂）。
        Assert.False(Totp.Verify(secret, null!, now));
        Assert.Equal(-1, Totp.MatchStep(secret, null!, now));

        // 重放：候选步不晚于已用步 → false / -1（重放臂）。
        Assert.False(Totp.Verify(secret, ComputeCode(secret, step), now, lastUsedStep: step + 1));
        Assert.Equal(-1, Totp.MatchStep(secret, ComputeCode(secret, step), now, lastUsedStep: step + 1));

        // 非法密钥 → MatchStep -1（Base32Decode null 臂）。
        Assert.Equal(-1, Totp.MatchStep("NOT!BASE32", "123456", now));
    }

    // ---- TotpService：用户缺失、空码、未启用重生成、密码复验用户缺失臂 ----

    [Fact]
    public void VerifyLogin_UserMissing_AfterEnabledCheck()
    {
        var (_, _, store, svc, _) = NewEnv();
        // 存储里有 enabled 账号，但用户库无此人 → Ok=false（用户缺失臂）。
        store.Save("ghost", new TotpAccount { Secret = Totp.GenerateSecret(), Enabled = true });
        var r = svc.VerifyLogin("ghost", "123456", "1.2.3.4", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.False(r.Ok);
        Assert.Null(r.Session);
    }

    [Fact]
    public void VerifyLogin_NullCode_RecoveryMiss_ThenInvalid()
    {
        var (_, _, store, svc, _) = NewEnv();
        // 恢复码哈希在但 code 为 null：恢复码 miss（code ?? "" 臂）→ MatchStep null 臂 → invalid。
        store.Save("alice", new TotpAccount
        {
            Secret = Totp.GenerateSecret(),
            Enabled = true,
            RecoveryCodeHashes = [AuthService.HashPassword("RECOV-1234")],
        });
        var r = svc.VerifyLogin("alice", null, "1.2.3.4", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.False(r.Ok);
    }

    [Fact]
    public void RegenerateRecovery_MissingOrDisabledAccount_ReturnsNotEnabled()
    {
        var (_, _, _, svc, _) = NewEnv();
        // 无账号：密码复验通过但账号缺失 → "totp not enabled"（null 臂）。
        var (c1, e1) = svc.RegenerateRecovery("alice", "correct-horse", "1.2.3.4", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Empty(c1);
        Assert.Equal("totp not enabled", e1);
        // 有账号未启用（Setup 后 Enabled=false）→ 同错误（!Enabled 臂）。
        svc.Setup("alice", "correct-horse", "1.2.3.4", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var (c2, e2) = svc.RegenerateRecovery("alice", "correct-horse", "1.2.3.4", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Empty(c2);
        Assert.Equal("totp not enabled", e2);
    }

    [Fact]
    public void Setup_GhostUser_ThrowsUnauthorized()
    {
        var (_, _, _, svc, _) = NewEnv();
        // 密码复验：用户不存在臂（account is null）→ 抛 UnauthorizedAccessException。
        Assert.Throws<UnauthorizedAccessException>(
            () => svc.Setup("ghost", "whatever", "1.2.3.4", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        // 密码 null → ?? "" 兜底臂，同样复验失败抛出。
        Assert.Throws<UnauthorizedAccessException>(
            () => svc.Setup("alice", null!, "1.2.3.4", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }
}

/// <summary>
/// 分支账批次四第五波（K8s 侧）：命名空间 Pod 列表、status 最小对象、
/// running/waiting 空原因臂、健康总览重启激增臂、最小事件映射、
/// 指标缺 metadata 与非法后缀臂、FindUnhealthy 缺省臂、巡检复位跨集群环。
/// </summary>
[Collection("Integration")]
public class Batch4Wave5K8sTests : IDisposable
{
    private readonly K8sFakeApiFactory _factory;
    private readonly HttpClient _client;
    private readonly string _dataDir;
    private readonly List<string> _seeded = new();

    public Batch4Wave5K8sTests()
    {
        _factory = new K8sFakeApiFactory();
        _factory.Api.StartAsync().GetAwaiter().GetResult();
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new("Bearer", _factory.LoginAdminAsync().GetAwaiter().GetResult());
        _dataDir = Environment.GetEnvironmentVariable("TAOOPS_DATA_DIR")!;
    }

    public void Dispose()
    {
        foreach (var name in _seeded) new KubeClusterStore(_dataDir).Remove(name, confirm: true);
        _factory.Dispose();
        _factory.Api.DisposeAsync().GetAwaiter().GetResult();
    }

    private void SeedCluster(string name)
    {
        new KubeClusterStore(_dataDir).Save(name, _factory.Api.Kubeconfig, "v1.29.0");
        _seeded.Add(name);
    }

    // ---- ListPods：带 ns 走命名空间列表臂；status:{} 与 running 状态臂 ----

    [Fact]
    public async Task Pods_NamespacedRoute_AndBareStatusArms()
    {
        SeedCluster("ns-pods");
        _factory.Api.OnJson("GET", "/api/v1/namespaces/default/pods", """
            {"items":[
              {"metadata":{"name":"st-bare","namespace":"default"},"status":{}},
              {"metadata":{"name":"run-1","namespace":"default"},
               "status":{"phase":"Running","containerStatuses":[
                 {"name":"c","ready":true,"restartCount":1,"state":{"running":{"startedAt":"2026-11-28T00:00:00Z"}}}]}},
              {"metadata":{"name":"wait-null","namespace":"default"},
               "status":{"phase":"Running","containerStatuses":[
                 {"name":"c","ready":false,"restartCount":0,"state":{"waiting":{}}}]}}
            ]}
            """);
        var res = await _client.GetAsync("/api/k8s/ns-pods/pods?ns=default");
        res.EnsureSuccessStatusCode();
        var items = (await res.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        Assert.Equal(3, items.Count);
        var bare = items.Single(x => x.GetProperty("name").GetString() == "st-bare");
        Assert.Equal(0, bare.GetProperty("restarts").GetInt32()); // status 有、containerStatuses 缺 → ?? 0 臂
        Assert.Equal("Unknown", bare.GetProperty("phase").GetString());
        var run = items.Single(x => x.GetProperty("name").GetString() == "run-1");
        Assert.True(run.GetProperty("ready").GetBoolean());
        Assert.True(run.GetProperty("reason").ValueKind == JsonValueKind.Null); // running 状态 → waiting 原因 null 臂
        var waitNull = items.Single(x => x.GetProperty("name").GetString() == "wait-null");
        Assert.True(waitNull.GetProperty("reason").ValueKind == JsonValueKind.Null); // waiting 空对象 → Reason null 臂
    }

    // ---- Health：重启激增（无 waiting 原因）臂 ----

    [Fact]
    public async Task Health_RestartSpike_WithoutWaitingReason()
    {
        SeedCluster("hl-spike");
        _factory.Api.OnJson("GET", "/api/v1/nodes", """{"items":[]}""");
        _factory.Api.OnJson("GET", "/api/v1/namespaces", """{"items":[{"metadata":{"name":"default"}}]}""");
        _factory.Api.OnJson("GET", "/api/v1/namespaces/default/pods", """
            {"items":[
              {"metadata":{"name":"spiky","namespace":"default"},
               "status":{"phase":"Running","containerStatuses":[
                 {"name":"c","ready":false,"restartCount":6,"state":{"running":{}}}]}},
              {"metadata":{"name":"calm","namespace":"default"},
               "status":{"phase":"Running","containerStatuses":[
                 {"name":"c","ready":true,"restartCount":1,"state":{"running":{}}}]}},
              {"metadata":{"name":"bare","namespace":"default"},"status":{}}
            ]}
            """);
        var res = await _client.GetAsync("/api/k8s/health");
        res.EnsureSuccessStatusCode();
        var entry = (await res.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()
            .Single(x => x.GetProperty("cluster").GetString() == "hl-spike");
        Assert.Equal(3, entry.GetProperty("podsTotal").GetInt32());
        var unhealthy = entry.GetProperty("unhealthyPods").EnumerateArray().Select(x => x.GetString()!).ToList();
        Assert.Contains("default/spiky", unhealthy); // Reason null 但 restarts=6 ≥ 5 → 激增臂
        Assert.DoesNotContain("default/calm", unhealthy);
        Assert.DoesNotContain("default/bare", unhealthy); // restarts 0 → 不标记
    }

    // ---- ListEvents：最小事件 → 各 ?? "" 臂 + 时间戳回落 ----

    [Fact]
    public async Task Events_MinimalItems_MapFallbacks()
    {
        SeedCluster("ev-min");
        _factory.Api.OnJson("GET", "/api/v1/events", """
            {"items":[
              {"metadata":{"name":"e1","namespace":"default","creationTimestamp":"2026-11-28T00:00:00Z"},
               "involvedObject":{"name":"pod-1"},"type":"Warning","reason":"BackOff",
               "message":"boom","lastTimestamp":"2026-11-28T01:00:00Z"},
              {"metadata":{"name":"e2","creationTimestamp":"2026-11-28T00:00:00Z"}},
              {"metadata":{"name":"e3"}}
            ]}
            """);
        var res = await _client.GetAsync("/api/k8s/ev-min/events");
        res.EnsureSuccessStatusCode();
        var items = (await res.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        Assert.Equal(3, items.Count);
        var full = items.Single(x => x.GetProperty("object").GetString() == "pod-1");
        Assert.Equal("Warning", full.GetProperty("type").GetString());
        Assert.True(full.GetProperty("lastTsMs").ValueKind == JsonValueKind.Number); // lastTimestamp 在 → 毫秒值
        // e2：各字段缺省 → ?? "" 臂；lastTimestamp 缺 → 回落 CreationTimestamp（非 null）。
        var minimal = items.Single(x => x.GetProperty("object").GetString() == ""
            && x.GetProperty("lastTsMs").ValueKind != JsonValueKind.Null);
        Assert.Equal("", minimal.GetProperty("namespace").GetString()); // 各 ?? "" 臂
        Assert.Equal("", minimal.GetProperty("type").GetString());
        Assert.Equal("", minimal.GetProperty("reason").GetString());
        Assert.Equal("", minimal.GetProperty("message").GetString());
        Assert.True(minimal.GetProperty("lastTsMs").ValueKind == JsonValueKind.Number); // 回落 CreationTimestamp
        var bare = items.Single(x => x.GetProperty("lastTsMs").ValueKind == JsonValueKind.Null);
        Assert.Equal("", bare.GetProperty("object").GetString());
    }

    // ---- ParsePodMetrics：item 缺 metadata、容器缺 name、非法后缀臂（纯函数直测） ----

    [Fact]
    public void ParsePodMetrics_MissingMetadata_AndInvalidSuffixes()
    {
        var result = K8sClusterService.ParsePodMetrics(new
        {
            items = new object[]
            {
                new { }, // 无 metadata → name/ns 双空串臂
                new { metadata = new { } }, // metadata 空对象 → name/ns 双缺臂
                new { metadata = new { name = (string?)null, @namespace = (string?)null } }, // 双 null → ?? "" 臂
                new
                {
                    metadata = new { name = "p1", @namespace = "ns1" },
                    containers = new object[]
                    {
                        new { usage = new { cpu = "xm", memory = "xi" } }, // m 后缀数字非法 → 0；内存空数字 → 0
                        new { usage = new { cpu = "xu" } }, // u 后缀非法 → 0
                        new { usage = new { cpu = "xn", memory = "abc" } }, // n 后缀非法 → 0
                        new { name = (string?)null, usage = new { cpu = "5m", memory = "1Ki" } }, // name null → "" 臂
                        new { }, // 无 name、无 usage → "" 与 0 臂
                    },
                },
            },
        });
        Assert.True(result.Available);
        Assert.Equal(4, result.Pods.Count);
        var bare = result.Pods[0];
        Assert.Equal("", bare.Name);
        Assert.Equal("", bare.Namespace);
        Assert.Empty(bare.Containers);
        Assert.Equal("", result.Pods[1].Name); // metadata 空对象 → 双缺臂
        Assert.Equal("", result.Pods[1].Namespace);
        Assert.Equal("", result.Pods[2].Name); // name null → ?? "" 臂
        Assert.Equal("", result.Pods[2].Namespace); // namespace null → ?? "" 臂
        var p1 = result.Pods[3];
        Assert.Equal(5, p1.CpuMilli); // 仅合法的 5m 入账，非法后缀全 0
        var unnamed = p1.Containers.First(x => x.CpuMilli == 5);
        Assert.Equal("", unnamed.Name); // name null → ""
        var empty = p1.Containers.Last();
        Assert.Equal("", empty.Name); // name 缺 → ""
        Assert.Equal(0, empty.CpuMilli);
    }

    // ---- FindUnhealthy：status 缺 containerStatuses、running/waiting 空原因、ns null 臂 ----

    [Fact]
    public void FindUnhealthy_DefaultArms()
    {
        var statusNoCs = new V1Pod
        {
            Metadata = new V1ObjectMeta { Name = "no-cs", NamespaceProperty = "ns1" },
            Status = new V1PodStatus(), // status 在、containerStatuses null → waiting null、restarts ?? 0
        };
        var running = new V1Pod
        {
            Metadata = new V1ObjectMeta { Name = "run", NamespaceProperty = "ns1" },
            Status = new V1PodStatus
            {
                ContainerStatuses = [new()
                {
                    Name = "c", Ready = true, RestartCount = 0,
                    State = new V1ContainerState { Running = new V1ContainerStateRunning() }, // State.Running → Waiting null
                }],
            },
        };
        var waitingNoReason = new V1Pod
        {
            Metadata = new V1ObjectMeta { Name = "wait-null", NamespaceProperty = "ns1" },
            Status = new V1PodStatus
            {
                ContainerStatuses = [new()
                {
                    Name = "c", Ready = false, RestartCount = 0,
                    State = new V1ContainerState { Waiting = new V1ContainerStateWaiting() }, // Waiting 在、Reason null
                }],
            },
        };
        var nsNullBackOff = new V1Pod
        {
            Metadata = new V1ObjectMeta { Name = "backoff" }, // namespace null → "" 臂
            Status = new V1PodStatus
            {
                ContainerStatuses = [new()
                {
                    Name = "c", Ready = false, RestartCount = 0,
                    State = new V1ContainerState { Waiting = new V1ContainerStateWaiting { Reason = "CrashLoopBackOff" } },
                }],
            },
        };
        var result = K8sMonitor.FindUnhealthy([statusNoCs, running, waitingNoReason, nsNullBackOff]);
        Assert.Single(result);
        Assert.Equal(("", "backoff", "CrashLoopBackOff"), result[0]);
    }

    // ---- ScanOnce 复位环：他集群 key 的 StartsWith false 臂（A 集群标记不被 B 集群扫描清除） ----

    [Fact]
    public async Task ScanOnce_ResetRing_PreservesOtherClusterMarkers()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"k8smon5-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        try
        {
            var clusters = new KubeClusterStore(dataDir);
            clusters.Save("monA", _factory.Api.Kubeconfig, "v1.29.0");
            clusters.Save("monB", _factory.Api.Kubeconfig, "v1.29.0");
            var monitor = new K8sMonitor(clusters, new K8sClientFactory(),
                new AlertCenter(new AlertStore(dataDir)), NullLogger<Services.K8s.K8sMonitor>.Instance);

            for (var i = 0; i < 2; i++) _factory.Api.OnJson("GET", "/api/v1/namespaces", """{"items":[{"metadata":{"name":"default"}}]}""");
            for (var i = 0; i < 2; i++) _factory.Api.OnJson("GET", "/api/v1/namespaces/default/pods", """
                {"items":[{"metadata":{"name":"bad","namespace":"default"},
                  "status":{"phase":"Running","containerStatuses":[
                    {"name":"c","ready":false,"restartCount":0,"state":{"waiting":{"reason":"CrashLoopBackOff"}}}]}}]}
                """);
            Assert.Equal(2, monitor.ScanOnce()); // A、B 各触发一条

            // 两集群都转健康：扫 A 时 monB 的 key 不以 "monA/" 开头（StartsWith false 臂），
            // monB 标记保留；扫 B 时清除自身。全程无新告警。
            _factory.Api.Reset();
            for (var i = 0; i < 2; i++) _factory.Api.OnJson("GET", "/api/v1/namespaces", """{"items":[{"metadata":{"name":"default"}}]}""");
            for (var i = 0; i < 2; i++) _factory.Api.OnJson("GET", "/api/v1/namespaces/default/pods", """{"items":[]}""");
            Assert.Equal(0, monitor.ScanOnce());
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }
}
