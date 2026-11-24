using System.Threading.Channels;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using TaoOps.Server.Alerts;
using TaoOps.Server.Audit;
using TaoOps.Server.Grpc;
using TaoOps.Server.Relay;
using TaoOps.Server.Services;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>节点间备份中转枢纽（FR-A13，契约 §7）：分片转发、磁盘预检拒收、
/// 分片/整包校验失败 nack、告警接线、流量统计与审计留痕。</summary>
public class RelayHubTests
{
    private sealed class ResultReader : ICommandResultReader
    {
        private readonly Channel<DispatchResult> _results = Channel.CreateUnbounded<DispatchResult>();
        public void Enqueue(DispatchResult result) => _results.Writer.TryWrite(result);
        public void Complete() => _results.Writer.TryComplete();
        public async Task<DispatchResult?> ReadNextAsync(CancellationToken ct)
        {
            while (await _results.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                if (_results.Reader.TryRead(out var r))
                    return r;
            return null;
        }
    }

    /// <summary>可编程回执桩：按 op 决定成败，并记录收到的全部请求。</summary>
    private sealed class ScriptedWriter(ResultReader reader) : ICommandRequestWriter
    {
        public List<DispatchRequest> Written { get; } = [];
        public Func<string, int> ExitCodeFor { get; set; } = _ => 0;

        public Task WriteAsync(DispatchRequest request, CancellationToken ct)
        {
            Written.Add(request);
            var op = request.Args.FirstOrDefault(a => a.StartsWith("op=")) ?? "";
            reader.Enqueue(new DispatchResult
            {
                RequestId = request.RequestId,
                ExitCode = ExitCodeFor(op),
                Output = ExitCodeFor(op) == 0 ? "ok" : "scripted failure",
            });
            return Task.CompletedTask;
        }
    }

    private sealed class ChunkStreamReader(params RelayChunk[] items) : IAsyncStreamReader<RelayChunk>
    {
        private int _pos;
        public Task<bool> MoveNext(CancellationToken ct) => Task.FromResult(_pos < items.Length);
        public RelayChunk Current => items[_pos++];
    }

    private sealed class AckStreamWriter : IServerStreamWriter<RelayAck>
    {
        public List<RelayAck> Written { get; } = [];
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(RelayAck message) { Written.Add(message); return Task.CompletedTask; }
        public Task WriteAsync(RelayAck message, CancellationToken ct) { Written.Add(message); return Task.CompletedTask; }
    }

    private static (RelayHub Hub, NodeRegistry Registry, NodeCommandHub Hub2, ScriptedWriter Writer, ResultReader Reader, AlertStore Alerts, AuditStore Audit)
        Create(Func<string, int>? exitCode = null, bool attachChannel = true)
    {
        var dir = Path.Combine(Path.GetTempPath(), "taoops-relay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var registry = new NodeRegistry();
        var hub = new NodeCommandHub();
        var alerts = new AlertStore(dir);
        var audit = new AuditStore(dir);
        var reader = new ResultReader();
        var writer = new ScriptedWriter(reader) { ExitCodeFor = exitCode ?? (_ => 0) };
        if (attachChannel)
        {
            _ = hub.AttachAsync("node-t", writer, reader, CancellationToken.None);
        }
        var relayHub = new RelayHub(hub, new AlertCenter(alerts), audit);
        return (relayHub, registry, hub, writer, reader, alerts, audit);
    }

    private static RelayChunk Chunk(int index, bool eof = false, byte[]? data = null) => new()
    {
        RelayId = "rl-1",
        TargetNodeId = "node-t",
        UploadId = "up-1",
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

    private static async Task<List<RelayAck>> RunAsync(RelayHub hub, params RelayChunk[] chunks)
    {
        var acks = new AckStreamWriter();
        await hub.HandleStream(new ChunkStreamReader(chunks), acks, TestCallContext());
        return acks.Written;
    }

    private static ServerCallContext TestCallContext() => new RelayTestCallContext();

    private sealed class RelayTestCallContext : ServerCallContext
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

    [Fact]
    public async Task HappyPath_BeginPutEof_AllAcked()
    {
        var (hub, _, _, writer, _, _, audit) = Create();
        var acks = await RunAsync(hub, Chunk(0, data: [1, 2, 3, 4]), Chunk(1, eof: true));

        Assert.Equal(2, acks.Count);
        Assert.All(acks, a => Assert.True(a.Ok));
        // 中转流量统计：仅数据分片计入（4 字节）。
        Assert.Equal(4, acks[1].RelayedBytes);
        // 下发序列：begin → put → assemble。
        var ops = writer.Written.Select(r => r.Args.First(a => a.StartsWith("op="))).ToArray();
        Assert.Equal(new[] { "op=relay_begin", "op=relay_put", "op=relay_assemble" }, ops);
        // blob 只在 put 上。
        Assert.True(writer.Written[1].Blob.Length > 0);
        Assert.Equal(0, writer.Written[0].Blob.Length);
        Assert.Equal(0, writer.Written[2].Blob.Length);
        // 审计留痕：三次下发均进哈希链。
        Assert.Equal(3, audit.Query().Count(e => e.Target == "/relay/up-1"));
    }

    [Fact]
    public async Task TargetNotConnected_NackWithError()
    {
        var (hub, _, _, _, _, _, _) = Create(attachChannel: false);
        // 不挂接命令通道：目标未连接。
        var acks = await RunAsync(hub, Chunk(0));
        Assert.Single(acks);
        Assert.False(acks[0].Ok);
        Assert.Contains("not connected", acks[0].Error);
    }

    [Fact]
    public async Task DiskPrecheckRejected_NackAndAlert()
    {
        var (hub, _, _, writer, _, alerts, _) = Create(op => op == "op=relay_begin" ? 1 : 0);
        var acks = await RunAsync(hub, Chunk(0));
        Assert.Single(acks);
        Assert.False(acks[0].Ok);
        Assert.Contains("begin rejected", acks[0].Error);
        // 备份失败告警（FR-A19 接线，warn 级）。
        var list = alerts.Query(level: "warn", source: "backup", nodeId: null);
        Assert.Single(list);
        Assert.Contains("disk precheck", list[0].Detail);
        // 预检拒收后不再下发 put。
        Assert.Single(writer.Written);
    }

    [Fact]
    public async Task ChunkRejected_NackWithoutAssemble()
    {
        var (hub, _, _, writer, _, _, _) = Create(op => op == "op=relay_put" ? 1 : 0);
        var acks = await RunAsync(hub, Chunk(0, data: [1]), Chunk(1, eof: true));
        Assert.Single(acks);
        Assert.False(acks[0].Ok);
        Assert.Contains("chunk 0 rejected", acks[0].Error);
        // begin + 一次 put，assemble 未下发。
        Assert.Equal(2, writer.Written.Count);
    }

    [Fact]
    public async Task AssembleFailure_NackAndAlert()
    {
        var (hub, _, _, writer, _, alerts, _) = Create(op => op == "op=relay_assemble" ? 1 : 0);
        var acks = await RunAsync(hub, Chunk(0, data: [1]), Chunk(1, eof: true));
        Assert.Equal(2, acks.Count);
        Assert.True(acks[0].Ok);
        Assert.False(acks[1].Ok);
        Assert.Contains("assemble failed", acks[1].Error);
        var list = alerts.Query(level: "warn", source: "backup", nodeId: null);
        Assert.Single(list);
        Assert.Contains("whole-archive verification failed", list[0].Detail);
    }

    [Fact]
    public async Task Stats_TrackRelaysAndBytes()
    {
        var (hub, _, _, _, _, _, _) = Create();
        await RunAsync(hub, Chunk(0, data: [1, 2]), Chunk(1, eof: true));
        var json = System.Text.Json.JsonSerializer.Serialize(hub.Stats());
        Assert.Contains("\"total_relayed_bytes\":2", json);
        Assert.Contains("\"status\":\"done\"", json);
        Assert.Contains("\"relay_id\":\"up-1\"", json);
    }

    [Fact]
    public async Task MissingFields_NackWithoutDispatch()
    {
        var (hub, _, _, writer, _, _, _) = Create();
        var bad = Chunk(0);
        bad.TargetNodeId = "";
        var acks = await RunAsync(hub, bad);
        Assert.Single(acks);
        Assert.False(acks[0].Ok);
        Assert.Empty(writer.Written);
    }
}
