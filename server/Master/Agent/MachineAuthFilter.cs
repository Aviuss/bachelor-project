using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;

namespace Master.Agent;

public sealed class MachineAuthFilter(NpgsqlDataSource db) : IEndpointFilter
{
    public const string MachineIdKey = "MachineId";

    private const string BearerPrefix = "Bearer ";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var header = context.HttpContext.Request.Headers.Authorization.ToString();
        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return Results.Unauthorized();

        var token = header[BearerPrefix.Length..].Trim();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));

        await using var connection = db.CreateConnection();
        var machineId = await connection.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT id FROM machines WHERE token_hash = @hash",
            new { hash });
        if (machineId is null)
            return Results.Unauthorized();

        context.HttpContext.Items[MachineIdKey] = machineId.Value;
        return await next(context);
    }
}
