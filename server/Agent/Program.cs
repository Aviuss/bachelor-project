using System;
using System.Data;
using System.IO;
using System.Runtime.InteropServices;

namespace Agent;

public class Program
{
    static async Task Main(string[] args)
    {
        using var stopping = new CancellationTokenSource();
        void Stop(PosixSignalContext context)
        {
            context.Cancel = true;
            stopping.Cancel();
        }
        using var onInterrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);
        using var onTerminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);

        var runners = new Runner.RunnerFactory(CreateS3Manager());
        using var heartbeat = new HeartbeatClient(Required("MASTER_AGENT_URL"), Required("AGENT_TOKEN"), runners);
        await heartbeat.RunAsync(stopping.Token);
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"{name} is not configured.");

    private static Shared.S3manager CreateS3Manager() => new(
        endpoint: Required("AWS_ENDPOINT_URL_S3"),
        region: Required("AWS_DEFAULT_REGION"),
        accessKey: Required("AWS_ACCESS_KEY_ID"),
        secretKey: Required("AWS_SECRET_ACCESS_KEY"),
        bucket: Required("S3_BUCKET"));

}



