using TaoOps.Agent;

var serverUrl = Environment.GetEnvironmentVariable("TAOOPS_SERVER") ?? "http://localhost:5241";
var agentId = Environment.GetEnvironmentVariable("TAOOPS_AGENT_ID") ?? Guid.NewGuid().ToString();

var worker = new AgentWorker(serverUrl, agentId, Metrics.MetricsCollector.Collect, Console.WriteLine);
Console.WriteLine($"TaoOps Agent {Metrics.SystemInfo.AgentVersion} -> {serverUrl}");
await worker.RunAsync(CancellationToken.None);
