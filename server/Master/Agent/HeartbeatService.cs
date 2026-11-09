using Dapper;
using Npgsql;
using Shared.Contracts;

namespace Master.Agent;

public static class HeartbeatService
{
    private static readonly TimeSpan LongPollTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan LongPollCheckInterval = TimeSpan.FromSeconds(2);

    public static async Task<HeartbeatResponse> HeartbeatAsync(HttpContext http, NpgsqlDataSource db)
    {
        var machineId = (Guid)http.Items[MachineAuthFilter.MachineIdKey]!;

        await using (var connection = db.CreateConnection())
        {
            await connection.ExecuteAsync(
                "UPDATE machines SET last_seen_at = now() WHERE id = @machineId",
                new { machineId });
        }

        var responseToAgent = await FindResponsesToAgentAsync(db, machineId);
        for (var waited = TimeSpan.Zero; responseToAgent.Count == 0 && waited < LongPollTimeout; waited += LongPollCheckInterval)
        {
            await Task.Delay(LongPollCheckInterval, http.RequestAborted);
            responseToAgent = await FindResponsesToAgentAsync(db, machineId);
        }

        return new HeartbeatResponse(responseToAgent);
    }

    private static async Task<IReadOnlyList<Assignment>> FindResponsesToAgentAsync(NpgsqlDataSource db, Guid machineId)
    {
        await using var connection = db.CreateConnection();
        var assignments = await connection.QueryAsync<Assignment>(
            """
            SELECT a.id          AS AttemptId,
                   t.name        AS JobType,
                   a.mem_mb      AS MemMb,
                   a.cpu         AS Cpu,
                   a.timeout_s   AS TimeoutS,
                   j.params::text AS ParamsJson
            FROM job_attempts a
            JOIN jobs j ON j.id = a.job_id
            JOIN job_types t ON t.id = j.job_type_id
            WHERE a.machine_id = @machineId AND a.status = 'assigned'
            ORDER BY a.id
            """,
            new { machineId });
        return assignments.AsList();
    }
}
