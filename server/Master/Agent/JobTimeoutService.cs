using Npgsql;

namespace Master.Agent;

public sealed class JobTimeoutService(NpgsqlDataSource db, ILogger<JobTimeoutService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var connection = await db.OpenConnectionAsync(stoppingToken);
                await using var transaction = await connection.BeginTransactionAsync(stoppingToken);
                await JobTimeout.UpdateAsync(connection, transaction);
                await transaction.CommitAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Job timeout check failed");
            }
        }
    }
}
