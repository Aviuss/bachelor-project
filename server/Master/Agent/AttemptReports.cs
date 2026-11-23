using System.Diagnostics;
using Dapper;
using Npgsql;
using Shared;
using Shared.Contracts;

namespace Master.Agent;

public static class AttemptReports
{
    public enum ProcessResponse
    {
        DO_NOTHING,
        CLOSE,
    }

    private sealed class AttemptStateInDB
    {
        public RunStatus? Current { get; init; }
        public int? JobFinishedByAttemptId { get; init; }
    }

    public static async Task<ProcessResponse> ProcessAsync(NpgsqlDataSource db, S3manager s3, Guid machineId, AttemptReport report)
    {
        await using var connection = await db.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await JobAttemptsLock.AcquireAsync(connection, transaction);

        var stateInDB = await connection.QuerySingleAsync<AttemptStateInDB>(
            """
            SELECT
                (SELECT status::text FROM job_attempts
                 WHERE id = @AttemptId AND job_id = @JobId AND machine_id = @machineId
                 FOR UPDATE) AS Current,
                (SELECT id FROM job_attempts
                 WHERE job_id = @JobId AND status = 'succeeded'
                 ORDER BY id
                 LIMIT 1) AS JobFinishedByAttemptId
            """,
            new { report.AttemptId, report.JobId, machineId }, transaction);

        var (result, s3Tasks) = await ProcessReport(connection, transaction, s3, stateInDB, report);
        await transaction.CommitAsync();
        await connection.CloseAsync();

        await Task.WhenAll(s3Tasks);
        return result;
    }

    private static async Task<(ProcessResponse, List<Task> S3Tasks)> ProcessReport(NpgsqlConnection connection, NpgsqlTransaction transaction, S3manager s3, AttemptStateInDB stateInDB, AttemptReport report)
    {
        List<Task> s3Tasks = [];

        switch (stateInDB.Current, stateInDB.JobFinishedByAttemptId, report.Status)
        {
            // duplicate heartbeat
            case (_, int winner, _) when winner == report.AttemptId:
                return (ProcessResponse.CLOSE, s3Tasks);

            // agent hallucination, delete garbage
            case (null, _, RunStatus.Succeeded):
                s3Tasks.AddRange(DeleteOutputs(s3, report));
                return (ProcessResponse.CLOSE, s3Tasks);

            // agent hallucination
            case (null, _, _):
                return (ProcessResponse.CLOSE, s3Tasks);

            // another attempt already won: this one lost, close it and discard its outputs
            case (_, not null, RunStatus.Succeeded):
                await CancelAttemptAsync(connection, transaction, report);
                s3Tasks.AddRange(DeleteOutputs(s3, report));
                return (ProcessResponse.CLOSE, s3Tasks);

            // another attempt already won: whatever this one reports (running/failed), its work is no longer needed
            case (_, not null, _):
                await CancelAttemptAsync(connection, transaction, report);
                return (ProcessResponse.CLOSE, s3Tasks);

            // ---- from here on no attempt has won the job yet ----

            // never assigned to this machine; can't really happen, as ready attempts have no machine_id
            case (RunStatus.Ready, null, _):
                return (ProcessResponse.CLOSE, s3Tasks);

            // closed by master (e.g. timed out by JobTimeout), but the agent finished anyway: accept the late success
            case (RunStatus.Failed or RunStatus.Cancelled, null, RunStatus.Succeeded):
                await AcceptSuccessAsync(connection, transaction, s3, report, s3Tasks);
                return (ProcessResponse.CLOSE, s3Tasks);

            // closed by master; whatever the agent says now changes nothing
            case (RunStatus.Failed or RunStatus.Cancelled, null, _):
                return (ProcessResponse.CLOSE, s3Tasks);

            case (RunStatus.Assigned, null, RunStatus.Running):
                await SaveCurrentReportStatus(connection, transaction, report);
                return (ProcessResponse.DO_NOTHING, s3Tasks);

            // nothing new: already recorded
            case (RunStatus.Assigned, null, RunStatus.Assigned):
                return (ProcessResponse.DO_NOTHING, s3Tasks);

            // nothing new: already recorded
            case (RunStatus.Running, null, RunStatus.Running):
                return (ProcessResponse.DO_NOTHING, s3Tasks);

            case (RunStatus.Assigned or RunStatus.Running, null, RunStatus.Succeeded):
                await AcceptSuccessAsync(connection, transaction, s3, report, s3Tasks);
                return (ProcessResponse.CLOSE, s3Tasks);

            case (RunStatus.Assigned or RunStatus.Running, null, RunStatus.Failed):
                await SaveCurrentReportStatus(connection, transaction, report);
                await JobReadiness.UpdateAsync(connection, transaction);
                return (ProcessResponse.CLOSE, s3Tasks);

            default:
                return (ProcessResponse.DO_NOTHING, s3Tasks);
        }
    }

    private static async Task AcceptSuccessAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, S3manager s3, AttemptReport report, List<Task> s3Tasks)
    {
        if (!await ClaimOutputsAsync(connection, transaction, s3, report))
        {
            var failed = report with { Status = RunStatus.Failed, Error = "reported outputs are missing in S3" };
            await SaveCurrentReportStatus(connection, transaction, failed);
            await JobReadiness.UpdateAsync(connection, transaction);
            s3Tasks.AddRange(DeleteOutputs(s3, report));
            return;
        }

        await SaveCurrentReportStatus(connection, transaction, report);
        await CancelOtherAttemptsAsync(connection, transaction, report);
        await JobReadiness.UpdateAsync(connection, transaction);
    }

    private static Task CancelAttemptAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, AttemptReport report)
    {
        return connection.ExecuteAsync(
            """
            UPDATE job_attempts
            SET status = 'cancelled', finished_at = COALESCE(finished_at, now())
            WHERE id = @AttemptId AND status IN ('ready', 'assigned', 'running')
            """,
            new { report.AttemptId },
            transaction);
    }

    private static Task SaveCurrentReportStatus(
        NpgsqlConnection connection, NpgsqlTransaction transaction, AttemptReport report)
    {
        return connection.ExecuteAsync(
            """
            UPDATE job_attempts
            SET status = @Status::attempt_status,
                error = @Error,
                started_at = COALESCE(started_at, @StartedAt, CASE WHEN @IsStarted THEN now() END),
                finished_at = COALESCE(@FinishedAt, CASE WHEN @IsFinal THEN now() END)
            WHERE id = @AttemptId
            """,
            new
            {
                report.AttemptId,
                Status = report.Status.ToString().ToLowerInvariant(),
                report.Error,
                StartedAt = report.StartedAt?.ToUniversalTime(),
                FinishedAt = report.FinishedAt?.ToUniversalTime(),
                IsStarted = report.Status is not (RunStatus.Ready or RunStatus.Assigned),
                IsFinal = report.Status is RunStatus.Succeeded or RunStatus.Failed or RunStatus.Cancelled,
            },
            transaction);
    }

    private static Task CancelOtherAttemptsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, AttemptReport report)
    {
        return connection.ExecuteAsync(
            """
            UPDATE job_attempts
            SET status = 'cancelled', finished_at = now()
            WHERE job_id = @JobId AND id <> @AttemptId
              AND status IN ('ready', 'assigned', 'running')
            """,
            new { report.JobId, report.AttemptId },
            transaction);
    }

    private static async Task<bool> ClaimOutputsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, S3manager s3, AttemptReport report)
    {
        bool[] exist = await Task.WhenAll(report.Outputs
            .OfType<S3Resource>()
            .Select(s3Output => s3.ResourceExists(s3Output.GetKey(report.AttemptId))));

        if (exist.Contains(false))
            return false;

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

        return true;
    }

    private static List<Task> DeleteOutputs(S3manager s3, AttemptReport report)
    {
        return report.Outputs
            .OfType<S3Resource>()
            .Select(s3Output => s3.DeleteResource(s3Output.GetKey(report.AttemptId)))
            .ToList();
    }
}
