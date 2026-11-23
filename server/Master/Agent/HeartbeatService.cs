using Dapper;
using Npgsql;
using Shared.Contracts;

namespace Master.Agent;

public static class HeartbeatService
{
    private static readonly TimeSpan LongPollTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan LongPollCheckInterval = TimeSpan.FromSeconds(2);

    public static async Task<HeartbeatResponse> HeartbeatAsync(
        HttpContext http, HeartbeatRequest? request, NpgsqlDataSource db)
    {
        var machineId = (Guid)http.Items[MachineAuthFilter.MachineIdKey]!;

        await using (var connection = db.CreateConnection())
        {
            await connection.ExecuteAsync(
                "UPDATE machines SET last_seen_at = now() WHERE id = @machineId",
                new { machineId });
        }

        var acknowledged = new List<AttemptKey>();
        foreach (var report in request?.Reports ?? [])
        {
            var processedReport = await AttemptReports.ProcessAsync(db, machineId, report);
            if (processedReport == AttemptReports.ProcessResponse.ACKNOWLEDGE)
                acknowledged.Add(new AttemptKey(report.JobId, report.AttemptId));
        }

        var assignments = await AssignmentQueries.FindAsync(db, machineId);
        for (var waited = TimeSpan.Zero;
             assignments.Count == 0 && acknowledged.Count == 0 && waited < LongPollTimeout;
             waited += LongPollCheckInterval)
        {
            await Task.Delay(LongPollCheckInterval, http.RequestAborted);
            assignments = await AssignmentQueries.FindAsync(db, machineId);
        }

        return new HeartbeatResponse(assignments, acknowledged);
    }
}
