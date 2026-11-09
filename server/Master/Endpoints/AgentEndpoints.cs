namespace Master.Endpoints;

public static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapAgentApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/agent/v1");

        api.MapGet("/ping", () => Results.Ok(new { api = "agent" }));

        return app;
    }
}
