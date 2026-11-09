namespace Master.Endpoints;

public static class PublicEndpoints
{
    public static IEndpointRouteBuilder MapPublicApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/ping", () => Results.Ok(new { api = "public" }));

        return app;
    }
}
