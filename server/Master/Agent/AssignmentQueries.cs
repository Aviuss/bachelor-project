using System.Text.Json;
using Dapper;
using Npgsql;
using Shared.Contracts;

namespace Master.Agent;

internal record ActiveAttemptRow(int AttemptId, string Status);

internal record AttemptRow(int AttemptId, int JobId, int JobTypeId, string ParamsJson);

internal record ResourceRow(
    Guid ResourceId, string Direction, string Name, bool IsList, string Format, string Location, int? ProducedBy, string? Value);

public static class AssignmentQueries
{
    public static async Task<IReadOnlyList<Assignment>> FindAsync(NpgsqlDataSource db, Guid machineId)
    {
        await using var connection = await db.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await connection.ExecuteAsync(
            "SELECT 1 FROM machines WHERE id = @machineId FOR UPDATE",
            new { machineId }, transaction);

        await JobAttemptsLock.AcquireAsync(connection, transaction);

        var active = await connection.QueryFirstOrDefaultAsync<ActiveAttemptRow>(
            """
            SELECT id AS AttemptId, status::text AS Status
            FROM job_attempts
            WHERE machine_id = @machineId AND status IN ('assigned', 'running')
            ORDER BY id
            LIMIT 1
            """,
            new { machineId }, transaction);

        if (active?.Status == "running")
            return [];

        int? attemptId = active?.AttemptId ?? await connection.QuerySingleOrDefaultAsync<int?>(
            """
            WITH picked AS (
                SELECT id FROM job_attempts
                WHERE status = 'ready'
                ORDER BY id
                LIMIT 1
                FOR UPDATE SKIP LOCKED
            )
            UPDATE job_attempts a
            SET status = 'assigned', machine_id = @machineId, assigned_at = now()
            FROM picked WHERE a.id = picked.id
            RETURNING a.id
            """,
            new { machineId }, transaction);

        if (attemptId is null)
            return [];

        var attempt = await connection.QuerySingleAsync<AttemptRow>(
            """
            SELECT a.id            AS AttemptId,
                   a.job_id        AS JobId,
                   j.job_type_id   AS JobTypeId,
                   j.params::text  AS ParamsJson
            FROM job_attempts a
            JOIN jobs j ON j.id = a.job_id
            WHERE a.id = @attemptId
            """,
            new { attemptId }, transaction);

        var resources = await connection.QueryAsync<ResourceRow>(
            """
            SELECT r.id                    AS ResourceId,
                   'in'                    AS Direction,
                   io.name                 AS Name,
                   io.is_list              AS IsList,
                   f.name                  AS Format,
                   io.location_id::text    AS Location,
                   r.produced_by           AS ProducedBy,
                   v.value::text           AS Value
            FROM job_inputs ji
            JOIN job_type_io io ON io.id = ji.job_type_io_id
            JOIN resource_formats f ON f.id = io.format_id
            JOIN resources r ON r.id = ji.resource_id
            LEFT JOIN resource_jsonb v ON v.id = r.id
            WHERE ji.job_id = @JobId AND io.direction = 'in'

            UNION ALL

            SELECT r.id, 'out', io.name, false, f.name, io.location_id::text, NULL::int, NULL::text
            FROM resources r
            JOIN job_type_io io ON io.id = r.producer_io_id
            JOIN resource_formats f ON f.id = io.format_id
            WHERE r.producer_job_id = @JobId AND io.direction = 'out'
            """,
            new { attempt.JobId }, transaction);

        await transaction.CommitAsync();

        return [new Assignment(attempt.JobId, attempt.AttemptId, attempt.JobTypeId, attempt.ParamsJson,
            resources.Select(ToResource).ToList())];
    }

    private static Resource ToResource(ResourceRow row)
    {
        var direction = row.Direction == "in" ? ResourceDirection.Input : ResourceDirection.Output;
        var format = Enum.Parse<ResourceFormat>(row.Format, ignoreCase: true);

        return row.Location switch
        {
            "s3" => new S3Resource(row.ResourceId, direction, row.Name, row.IsList, format, row.ProducedBy),
            "db" => new DbResource(row.ResourceId, direction, row.Name, row.IsList, format,
                JsonSerializer.Deserialize<JsonElement>(row.Value is null ? "{}" : $$"""{"value":{{row.Value}}}""")),
            _ => throw new InvalidOperationException($"Unknown resource location '{row.Location}'."),
        };
    }
}
