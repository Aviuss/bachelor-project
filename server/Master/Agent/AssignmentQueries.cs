using System.Text.Json;
using Dapper;
using Npgsql;
using Shared.Contracts;

namespace Master.Agent;

internal record AttemptRow(int AttemptId, int JobId, int JobTypeId, string ParamsJson);

internal record ResourceRow(int JobId, Guid ResourceId, string Direction, string Name, string Format, string Location, int? ProducedBy, string? Value);

public static class AssignmentQueries
{
    public static async Task<IReadOnlyList<Assignment>> FindAsync(NpgsqlDataSource db, Guid machineId)
    {
        return [];
    }

    private static Resource ToResource(ResourceRow row)
    {
        var direction = row.Direction == "in" ? ResourceDirection.Input : ResourceDirection.Output;
        var format = Enum.Parse<ResourceFormat>(row.Format, ignoreCase: true);

        return row.Location switch
        {
            "s3" => new S3Resource(row.ResourceId, direction, row.Name, format, row.ProducedBy),
            "db" => new DbResource(row.ResourceId, direction, row.Name, format,
                JsonSerializer.Deserialize<JsonElement>(row.Value is null ? "{}" : $$"""{"value":{{row.Value}}}""")),
            _ => throw new InvalidOperationException($"Unknown resource location '{row.Location}'."),
        };
    }
}
