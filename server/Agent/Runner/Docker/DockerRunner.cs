using System;
using Shared.Contracts;
using Docker.DotNet;
using Docker.DotNet.Models;


namespace Agent.Runner.Docker;

public abstract class DockerRunner : AbstractRunner
{
    protected string? ContainerInputDir { get; set; } = null;
    protected string? ContainerOutputDir { get; set; } = null;

    private readonly DockerClient _client;

    private readonly string _imageName;

    protected List<string>? Entrypoint { get; set; }

    protected List<string>? Cmd { get; set; }

    protected DockerRunner(Assignment assignment, string imageName) : base(assignment)
    {
        _imageName = imageName;

        var uri = OperatingSystem.IsWindows()
            ? new Uri("npipe://./pipe/docker_engine")
            : new Uri("unix:///var/run/docker.sock");

        _client = new DockerClientBuilder().WithEndpoint(uri).Build();
    }


    protected override async Task Run()
    {
        if (InputPath is null || OutputPath is null)
            throw new InvalidOperationException("Folders have not been prepared. Call PrepareFolders() first.");

        
        List<string> binds = [];
        if (InputPath is not null)
        {
            string absoluteInputPath = Path.GetFullPath(InputPath).Replace("\\", "/");
            string bindConfigIn = $"{absoluteInputPath}:{ContainerInputDir}:z";
            binds.Add(bindConfigIn);            
        }
        if (OutputPath is not null)
        {
            string absoluteOutputPath = Path.GetFullPath(OutputPath).Replace("\\", "/");
            string bindConfigOut = $"{absoluteOutputPath}:{ContainerOutputDir}:z";
            binds.Add(bindConfigOut);
        }
        
        Console.WriteLine($"[Docker] Tworzenie kontenera");

        var response = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = _imageName,
            Entrypoint = Entrypoint ?? [],
            Cmd = Cmd ?? [],
            HostConfig = new HostConfig
            {
                Binds = binds
            }
        });

        string containerId = response.ID;

        if (string.IsNullOrEmpty(containerId))
            throw new InvalidOperationException("Container was not created.");

        try
        {
            await _client.Containers.StartContainerAsync(containerId, new ContainerStartParameters());
            Console.WriteLine("[Docker] Kontener wystartował.");

            var waitResponse = await _client.Containers.WaitContainerAsync(containerId);

            using var multiplexedStream = await _client.Containers.GetContainerLogsAsync(
                containerId,
                new ContainerLogsParameters
                {
                    ShowStdout = true,
                    ShowStderr = true
                },
                CancellationToken.None
            );

            var (stdout, stderr) = await multiplexedStream.ReadOutputToEndAsync(CancellationToken.None);

            Console.WriteLine("\n--- Wynik wykonania w kontenerze ---");
            if (!string.IsNullOrEmpty(stdout)) Console.WriteLine($"STDOUT: {stdout.Trim()}");
            if (!string.IsNullOrEmpty(stderr)) Console.WriteLine($"STDERR: {stderr.Trim()}");
            Console.WriteLine("------------------------------------\n");

            if (waitResponse.StatusCode != 0)
                throw new InvalidOperationException($"Container exited with code {waitResponse.StatusCode}.");
        }
        finally
        {
            await _client.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true });
            Console.WriteLine("[Docker] Kontener usunięty.");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _client.Dispose();

        base.Dispose(disposing);
    }
}
