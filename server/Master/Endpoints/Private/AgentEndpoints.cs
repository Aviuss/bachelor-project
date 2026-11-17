using Master.Agent;

namespace Master.Endpoints.Private;

public static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapAgentApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/agent/v1");

        api.MapGet("/ping", () => Results.Ok(new { api = "agent" }));

        api.MapPost("/heartbeat", HeartbeatService.HeartbeatAsync)
           .AddEndpointFilter<MachineAuthFilter>();

        return app;
    }
}
