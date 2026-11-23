using Dapper;
using Npgsql;

namespace Master.Agent;

public static class JobTimeout
{
    public static async Task UpdateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction) {
        await connection.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(hashtext('job_timeout'))",
            transaction: transaction);

        int timedOut = await connection.ExecuteAsync(
            """
            UPDATE job_attempts
            SET status = 'failed',
                error = 'timed out after ' || timeout_s || 's',
                finished_at = now()
            WHERE status IN ('assigned', 'running')
              AND assigned_at + make_interval(secs => timeout_s) < now();
            """,
            transaction: transaction);

        if (timedOut > 0)
            await JobReadiness.UpdateAsync(connection, transaction);
    }
}
