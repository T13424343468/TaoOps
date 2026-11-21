using TaoOps.Server.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddGrpc();
builder.Services.AddSingleton<NodeRegistry>();

var app = builder.Build();
app.MapGrpcService<AgentService>();
app.MapGet("/", () => "TaoOps Server M1");

app.Run();
