using System;
using System.Data;
using System.IO;
using System.Runtime.InteropServices;

namespace Agent;

public class Program
{
    static async Task Main(string[] args)
    {
        if (args is ["docker-test"])
        {
            await RunDockerTestAsync();
            return;
        }

        using var stopping = new CancellationTokenSource();
        void Stop(PosixSignalContext context)
        {
            context.Cancel = true;
            stopping.Cancel();
        }
        using var onInterrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);
        using var onTerminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);

        using var heartbeat = new HeartbeatClient(Required("MASTER_AGENT_URL"), Required("AGENT_TOKEN"));
        await heartbeat.RunAsync(stopping.Token);
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"{name} is not configured.");

    private static async Task RunDockerTestAsync()
    {
        string job_id = "123456789";
        using var runner = new Jobs.DockerRunner(job_id, "alpine:3.20", "{\"processes\"...");
        runner.PrepareFolders(); //aby stworzylo plik config.json i wczytalo parametry
        await runner.RunAsync();
        await runner.VerifyLogsAsync();
        await runner.CleanupAsync();
    }
}



