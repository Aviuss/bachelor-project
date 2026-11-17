using Master.Pipelines;

namespace Master.Endpoints.Public;

public static class PublicEndpoints
{
    public static IEndpointRouteBuilder MapPublicApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/ping", () => Results.Ok(new { api = "public" }));

        api.MapPost("/requests", RequestService.CreateAsync);
        api.MapPut("/requests/{requestId:guid}/uploads/{resourceId:guid}", RequestService.UploadAsync);

        return app;
    }
}
