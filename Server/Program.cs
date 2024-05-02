using KcpGameServer;
using KcpGameServer.Models;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateSlimBuilder(args);

// builder.Services.ConfigureHttpJsonOptions(options =>
// {
//     options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
// });

var builderConfiguration = builder.Configuration;

// Config url.

var httpAddress = builderConfiguration.GetValue<string>("HttpAddress");
var httpPort = builderConfiguration.GetValue<ushort>("HttpPort");
var useHttps = builderConfiguration.GetValue<bool>("UseHttps");

if (useHttps) builder.WebHost.UseKestrelHttpsConfiguration();

builder.WebHost.UseUrls($"{(useHttps ? "https" : "http")}://{httpAddress}:{httpPort}");

// Add services to the container.

var app = builder.Build();

var sessionApi = app.MapGroup("/session");

var sessionManager = new SessionManager(builderConfiguration);

// TODO: Change JSON to MemoryPack

sessionApi.MapGet(
    "/list",
    () => sessionManager.ListSessions()
);

sessionApi.MapPost(
    "/join",
    ([FromBody] ulong sessionId) => sessionManager.JoinSession(sessionId)
);

sessionApi.MapPost(
    "/allocate",
    ([FromBody] SessionInfoModel sessionInfoModel) => sessionManager.AllocateSession(sessionInfoModel)
);

sessionApi.MapPost(
    "/modify",
    ([FromBody] SessionInfoModel sessionInfoModel) => sessionManager.ModifySession(sessionInfoModel)
);

app.Run();
