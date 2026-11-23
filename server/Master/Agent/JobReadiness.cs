using Dapper;
using Npgsql;

namespace Master.Agent;

public static class JobReadiness
{
    private const int DefaultMemMb = 1024;
    private const decimal DefaultCpu = 1.0m;
    private const int DefaultTimeoutS = 3600;
    private const int MaxAttempts = 3;

    public static async Task UpdateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction) {
        await JobAttemptsLock.AcquireAsync(connection, transaction);

        await connection.ExecuteAsync(
            """
            INSERT INTO job_attempts (job_id, status, mem_mb, cpu, timeout_s)
            SELECT job.id, 'ready'::attempt_status, @DefaultMemMb, @DefaultCpu, @DefaultTimeoutS
            FROM jobs AS job
            WHERE job.id NOT IN (SELECT job_id FROM job_attempts WHERE status <> 'failed')
              AND (SELECT COUNT(*) FROM job_attempts WHERE job_id = job.id AND status = 'failed') < @MaxAttempts
              AND 'available' = ALL (
                  SELECT resource.status
                  FROM job_inputs AS link
                  JOIN resources AS resource ON resource.id = link.resource_id
                  WHERE link.job_id = job.id);
            """,
            new { DefaultMemMb, DefaultCpu, DefaultTimeoutS, MaxAttempts },
            transaction);
    }
}
