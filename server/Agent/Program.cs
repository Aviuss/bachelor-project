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
        var assignment = new Shared.Contracts.Assignment(
            JobId: 1,
            AttemptId: 123456789,
            JobTypeId: 1,
            ParamsJson: "{\"processes\"...",
            Resources: []);

        await Runner.RunnerFactory.RunAsync(assignment);

        var run = Runner.RunnerFactory.Runs[(assignment.JobId, assignment.AttemptId)];
        Console.WriteLine($"Status: {run.Status}, zakończone: {run.IsFinished}");
    }
}



