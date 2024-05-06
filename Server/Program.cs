using System.Diagnostics.CodeAnalysis;
using KcpGameServer;
using KcpGameServer.Models;
using MemoryPack;

var builder = WebApplication.CreateSlimBuilder(args);


var builderConfiguration = builder.Configuration;

// Config url.

var httpAddress = builderConfiguration.GetValue<string>("HttpAddress");
var httpPort = builderConfiguration.GetValue<ushort>("HttpPort");
var useHttps = builderConfiguration.GetValue<bool>("UseHttps");

if (useHttps) builder.WebHost.UseKestrelHttpsConfiguration();

// Add services to the container.
var sessionManager = new SessionManager(builderConfiguration);

var app = builder.Build();
var sessionApi = app.MapGroup("/session");

sessionApi.MapGet(
    "/list",
    () =>
    {
        var sessions = sessionManager.ListSessions();
        return Results.Bytes(MemoryPackSerializer.Serialize(sessions));
    }
);

sessionApi.MapPost(
    "/join",
    async (HttpContext context) =>
    {
        var (sessionId, success) = await DeserializeBody<ulong>(context);
        if (!success) return Results.BadRequest();
        var token = sessionManager.JoinSession(sessionId);
        return Results.Bytes(MemoryPackSerializer.Serialize(token));
    }
);

sessionApi.MapPost(
    "/allocate",
    async (HttpContext context) =>
    {
        var (sessionInfoModel, success) = await DeserializeBody<SessionInfoModel>(context);
        if (!success) return Results.BadRequest();
        var token = sessionManager.AllocateSession(sessionInfoModel);
        return Results.Bytes(MemoryPackSerializer.Serialize(token));
    }
);

sessionApi.MapPost(
    "/modify",
    async (HttpContext context) =>
    {
        var (sessionInfoModel, success) = await DeserializeBody<SessionInfoModel>(context);
        if (!success) return Results.BadRequest();
        var token = sessionManager.ModifySession(sessionInfoModel);
        return Results.Bytes(MemoryPackSerializer.Serialize(token));
    }
);

await app.RunAsync($"{(useHttps ? "https" : "http")}://{httpAddress}:{httpPort}");

return;

static async Task<(T? Value, bool Success)> DeserializeBody<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(HttpContext context)
{
    var inArgument = await MemoryPackSerializer.DeserializeAsync<T>(context.Request.Body);
    if (inArgument is null)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return (default, false);
    }

    return (inArgument, true);
}