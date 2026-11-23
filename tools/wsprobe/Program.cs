// E2E 探针（M2）：验证中控 WebSocket 终端/日志桥接。
// 用法：
//   wsprobe term  <ws-url> <session> <command...>   连接终端 WS，发送命令行，读回显直到出现命令输出
//   wsprobe logs  <ws-url> <expect> [seconds]  连接日志 WS，断言 expect 出现在流中
// 退出码 0=PASS，1=FAIL；证书校验跳过（仅测试工具）。
using System.Net.Security;
using System.Net.WebSockets;
using System.Text;

if (args.Length < 2)
{
    Console.WriteLine("FAIL: usage: wsprobe term|logs ...");
    return 1;
}

using var http = new HttpClient(new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
});

return args[0] switch
{
    "term" => await ProbeTerm(args),
    "logs" => await ProbeLogs(args),
    _ => Fail("unknown mode " + args[0]),
};

static int Fail(string msg)
{
    Console.WriteLine("FAIL: " + msg);
    return 1;
}

static ClientWebSocket NewWs()
{
    var ws = new ClientWebSocket();
    ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
    ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(5);
    return ws;
}

static async Task<string> ReceiveAll(ClientWebSocket ws, CancellationToken ct)
{
    var sb = new StringBuilder();
    var buffer = new byte[64 * 1024];
    while (true)
    {
        var result = await ws.ReceiveAsync(buffer, ct);
        if (result.MessageType == WebSocketMessageType.Close || result.Count == 0)
            break;
        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        if (result.EndOfMessage)
            break;
    }
    return sb.ToString();
}

static async Task<int> ProbeTerm(string[] args)
{
    if (args.Length < 4) return Fail("usage: wsprobe term <ws-url> <session> <command...>");
    var url = args[1];
    var command = string.Join(' ', args.Skip(3)) + "\n";
    using var ws = NewWs();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    try
    {
        await ws.ConnectAsync(new Uri(url), cts.Token);
        await ws.SendAsync(Encoding.UTF8.GetBytes(command), WebSocketMessageType.Binary, true, cts.Token);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var seen = new StringBuilder();
        while (DateTime.UtcNow < deadline)
        {
            // 取消未完成的 ReceiveAsync 会把 ClientWebSocket 置为 Aborted，
            // 因此等待预算用"距截止的剩余时间"，到点自然退出，不做周期性打断。
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            wait.CancelAfter(deadline - DateTime.UtcNow);
            string text;
            try
            {
                text = await ReceiveAll(ws, wait.Token);
            }
            catch (OperationCanceledException) when (!cts.Token.IsCancellationRequested)
            {
                break; // 截止时间到，正常收尾判定
            }
            seen.Append(text);
            // 命令回显 + 输出都到达即视为通过（bash -i 会回显输入）
            var sofar = seen.ToString();
            if (sofar.Contains("ws-ok") && sofar.Contains("echo ws-ok"))
            {
                Console.WriteLine($"PASS term echo: {sofar.Trim().Replace("\n", " | ")}");
                return 0;
            }
        }
        return Fail($"term echo not observed; got: {seen}");
    }
    catch (Exception ex)
    {
        return Fail("term: " + ex.Message);
    }
}

static async Task<int> ProbeLogs(string[] args)
{
    if (args.Length < 3) return Fail("usage: wsprobe logs <ws-url> <expect> [seconds]");
    var url = args[1];
    var expect = args[2];
    var seconds = args.Length > 3 ? int.Parse(args[3]) : 15;
    using var ws = NewWs();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 10));
    try
    {
        await ws.ConnectAsync(new Uri(url), cts.Token);
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        var seen = new StringBuilder();
        while (DateTime.UtcNow < deadline)
        {
            // 同 ProbeTerm：周期性打断会把连接打成 Aborted，改用剩余时间预算。
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            wait.CancelAfter(deadline - DateTime.UtcNow);
            string text;
            try
            {
                text = await ReceiveAll(ws, wait.Token);
            }
            catch (OperationCanceledException) when (!cts.Token.IsCancellationRequested)
            {
                break; // 截止时间到，正常收尾判定
            }
            seen.Append(text);
            if (seen.ToString().Contains(expect))
            {
                Console.WriteLine($"PASS logs: observed {expect}");
                return 0;
            }
        }
        return Fail($"logs: {expect} not observed; got: {seen}");
    }
    catch (Exception ex)
    {
        return Fail("logs: " + ex.Message);
    }
}
