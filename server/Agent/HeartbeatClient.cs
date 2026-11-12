using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Shared.Contracts;

namespace Agent;

class HeartbeatClient : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);

    private readonly HttpClient _client;

    public HeartbeatClient(string masterUrl, string token)
    {
        _client = new HttpClient { BaseAddress = new Uri(masterUrl), Timeout = RequestTimeout };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
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
        try
        {
            using var response = await _client.PostAsync("/agent/v1/heartbeat", null, cancellationToken);
            response.EnsureSuccessStatusCode();

            var heartbeat = await response.Content.ReadFromJsonAsync<HeartbeatResponse>(cancellationToken);
            var jobs = heartbeat!.Assignments;
            foreach (var job in jobs)
            {
                Console.WriteLine(
                    $"[Heartbeat] Job {job.AttemptId}: {job.JobType} ({job.MemMb} MB, {job.Cpu} CPU, timeout {job.TimeoutS} s)");
            }

            return TimeSpan.Zero;
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested
                                  && e is HttpRequestException or TaskCanceledException or JsonException)
        {
            Console.Error.WriteLine($"[Heartbeat] Failed, retrying in {RetryDelay.TotalSeconds} s: {e.Message}");
            return RetryDelay;
        }
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
