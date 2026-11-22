using Grpc.Core;
using System.Collections.Concurrent;
using TaoOps.Server.Grpc;

namespace TaoOps.Server.Services;

/// <summary>
/// 节点注册表：agent_id → 节点状态。并发安全；M1 内存态，M2 落 SQLite。
/// </summary>
public class NodeRegistry
{
    private readonly ConcurrentDictionary<string, NodeState> _nodes = new();

    public sealed class NodeState
    {
        public required string NodeId { get; init; }
        public required string AgentId { get; init; }
        public required string Hostname { get; init; }
        public DateTimeOffset LastSeen { get; set; }
        public DateTimeOffset RegisteredAt { get; init; }
    }

    /// <summary>注册幂等：同一 agent_id 返回同一 node_id。</summary>
    public NodeState Register(RegisterRequest req)
    {
        var now = DateTimeOffset.UtcNow;
        var state = _nodes.GetOrAdd(req.AgentId, _ => new NodeState
        {
            NodeId = Guid.NewGuid().ToString("N"),
            AgentId = req.AgentId,
            Hostname = req.Hostname,
            RegisteredAt = now,
            LastSeen = now,
        });
        state.LastSeen = now;
        return state;
    }

    public NodeState? GetByNodeId(string nodeId) =>
        _nodes.Values.FirstOrDefault(n => n.NodeId == nodeId);

    /// <summary>全部已知节点（自动订阅巡检用；顺序不承诺稳定）。</summary>
    public IEnumerable<NodeState> All() => _nodes.Values;

    public bool Touch(string nodeId)
    {
        var node = GetByNodeId(nodeId);
        if (node is null) return false;
        node.LastSeen = DateTimeOffset.UtcNow;
        return true;
    }

    public int Count => _nodes.Count;
}
