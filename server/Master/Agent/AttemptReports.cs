using Dapper;
using Master.Pipelines;
using Npgsql;
using Shared.Contracts;

namespace Master.Agent;

public static class AttemptReports
{
    public static async Task<bool> ApplyAsync(NpgsqlDataSource db, Guid machineId, AttemptReport report)
    {
        bool isFinal = report.Status is RunStatus.Succeeded or RunStatus.Failed;

        await using var connection = await db.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var current = await connection.QuerySingleOrDefaultAsync<string?>(
            """
            SELECT status::text FROM job_attempts
            WHERE id = @AttemptId AND job_id = @JobId AND machine_id = @machineId
            FOR UPDATE
            """,
            new { report.AttemptId, report.JobId, machineId }, transaction);

        if (current == "assigned" && report.Status == RunStatus.Running)
        {
            await connection.ExecuteAsync(
                "UPDATE job_attempts SET status = 'running', started_at = COALESCE(@StartedAt, now()) WHERE id = @AttemptId",
                new { report.AttemptId, StartedAt = report.StartedAt?.ToUniversalTime() }, transaction);
        }
        else if ((current is "assigned" or "running") && isFinal)
        {
            if (report.Status == RunStatus.Succeeded)
            {
                foreach (var output in report.Outputs)
                {
                    int claimed = await connection.ExecuteAsync(
                        """
                        UPDATE resources SET produced_by = @AttemptId, status = 'available'
                        WHERE id = @ResourceId AND producer_job_id = @JobId AND status = 'pending'
                        """,
                        new { report.AttemptId, output.ResourceId, report.JobId }, transaction);

                    if (claimed == 1 && output is DbResource dbOutput)
                    {
                        await connection.ExecuteAsync(
                            "INSERT INTO resource_jsonb (id, value) VALUES (@ResourceId, @Value::jsonb)",
                            new { output.ResourceId, Value = dbOutput.Value.GetProperty("value").GetRawText() }, transaction);
                    }
                }
            }

            await connection.ExecuteAsync(
                """
                UPDATE job_attempts
                SET status = @Status::attempt_status,
                    error = @Error,
                    started_at = COALESCE(started_at, @StartedAt),
                    finished_at = COALESCE(@FinishedAt, now())
                WHERE id = @AttemptId
                """,
                new
                {
                    report.AttemptId,
                    Status = report.Status == RunStatus.Succeeded ? "succeeded" : "failed",
                    report.Error,
                    StartedAt = report.StartedAt?.ToUniversalTime(),
                    FinishedAt = report.FinishedAt?.ToUniversalTime(),
                },
                transaction);

            await JobReadiness.UpdateAsync(connection, transaction);
        }

        await transaction.CommitAsync();
        return isFinal;
    }
}
