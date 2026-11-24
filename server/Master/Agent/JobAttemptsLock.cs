using Dapper;
using Npgsql;

namespace Master.Agent;

public static class JobAttemptsLock
{
    public static Task AcquireAsync(NpgsqlConnection connection, NpgsqlTransaction transaction) =>
        connection.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtext('job_attempts'))", transaction: transaction);
}
