using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agent.Runner;
using Shared.Contracts;

namespace Agent;

class HeartbeatClient : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);

    private readonly HttpClient _client;
    private readonly RunnerFactory _runners;

    public HeartbeatClient(string masterUrl, string token, RunnerFactory runners)
    {
        _client = new HttpClient { BaseAddress = new Uri(masterUrl), Timeout = RequestTimeout };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _runners = runners;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine($"[Heartbeat] Sending heartbeats to {_client.BaseAddress}");
        try
        {
            while (true)
            {
                var delay = await SendHeartbeatAsync(cancellationToken);
                await Task.Delay(delay, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine("[Heartbeat] Stopped.");
        }
    }

    private async Task<TimeSpan> SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        HeartbeatResponse heartbeat;
        try
        {
            var request = new HeartbeatRequest(_runners.GetReports());
            using var response = await _client.PostAsJsonAsync("/agent/v1/heartbeat", request, cancellationToken);
            response.EnsureSuccessStatusCode();

            heartbeat = (await response.Content.ReadFromJsonAsync<HeartbeatResponse>(cancellationToken))!;
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested
                                  && e is HttpRequestException or TaskCanceledException or JsonException)
        {
            Console.Error.WriteLine($"[Heartbeat] Failed, retrying in {RetryDelay.TotalSeconds} s: {e.Message}");
            return RetryDelay;
        }

        foreach (var ack in heartbeat.Acknowledged)
        {
            try
            {
                _runners.Acknowledge((ack.JobId, ack.AttemptId));
                Console.WriteLine($"[Heartbeat] Attempt {ack.AttemptId} of job {ack.JobId} acknowledged.");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[Heartbeat] Cleanup of attempt {ack.AttemptId} failed: {e.Message}");
            }
        }

        foreach (var assignment in heartbeat.Assignments)
        {
            Console.WriteLine(
                $"[Heartbeat] Attempt {assignment.AttemptId} of job {assignment.JobId} (type {assignment.JobTypeId}), {assignment.Resources.Count} resources");
            _ = _runners.RunAsync(assignment);
        }

        return TimeSpan.Zero;
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
