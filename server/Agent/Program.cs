using System;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace Agent;

public class Program
{
    static async Task Main(string[] args)
    {
        string job_id = "123456789";
        using var runner = new Runner(job_id, "alpine:3.20", "{\"processes\"...");
        runner.PrepareFolders(); //aby stworzylo plik config.json i wczytalo parametry
        await runner.RunAsync();
        await runner.VerifyLogsAsync();
        await runner.CleanupAsync();
    }
}

class Runner : IDisposable
{
    private readonly DockerClient _client;
    private string? _containerId;

    public string ImageName { get; set; }
    public string? InputPath { get; set; }
    public string? OutputPath { get; set; }

    public string JobId;

    public string Parameters;

    public Runner (string jobId, string imageName, string parameters)
    {
        ImageName = imageName;
        JobId = jobId;
        Parameters = parameters;

        var uri = OperatingSystem.IsWindows()
            ? new Uri("npipe://./pipe/docker_engine")
            : new Uri("unix:///var/run/docker.sock");

        _client = new DockerClientConfiguration(uri).CreateClient();
    }


    public void PrepareFolders() 
    { 
        string project_folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..");
        string path = Path.Combine(project_folder, JobId);
        string inputFolder = Path.Combine(path, "input");
        string outputFolder = Path.Combine(path, "output");

    
        Directory.CreateDirectory(inputFolder);
        Directory.CreateDirectory(outputFolder);

        // Aktualizujemy InputPath, aby CreateContainerWithMountAsync wiedział, skąd montować pliki
        InputPath = inputFolder;
        OutputPath = outputFolder;

        string configFile = Path.Combine(inputFolder, "config.json");
        File.WriteAllText(configFile, Parameters);

        Console.WriteLine($"Utworzono strukturę folderów dla id {JobId}");
    }


    public async Task RunAsync()
    {
        string absoluteInputPath = Path.GetFullPath(InputPath).Replace("\\", "/");
        string absoluteOutputPath = Path.GetFullPath(OutputPath).Replace("\\", "/");

        const string containerInputDir = "/app/input";
        const string containerOutputDir = "/app/output";

        string bindConfigIn = $"{absoluteInputPath}:{containerInputDir}:ro";
        string bindConfigOut = $"{absoluteOutputPath}:{containerOutputDir}:rw";

        Console.WriteLine($"[Docker] Tworzenie kontenera z montowaniem: {bindConfigIn} i {bindConfigOut}");


        var response = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = ImageName,
            Cmd = new[] {"cat", "app/output/tekst.txt"},
            HostConfig = new HostConfig
            {
                Binds = new[] { bindConfigIn, bindConfigOut }
            }
        });

        _containerId = response.ID;
    
        if (string.IsNullOrEmpty(_containerId))
            throw new InvalidOperationException("Kontener nie został utworzony.");

        await _client.Containers.StartContainerAsync(_containerId, new ContainerStartParameters());
        Console.WriteLine("[Docker] Kontener wystartował.");
    }

    public async Task VerifyLogsAsync()
    {
        if (string.IsNullOrEmpty(_containerId))
            throw new InvalidOperationException("Kontener nie istnieje.");

        await _client.Containers.WaitContainerAsync(_containerId);

        using var multiplexedStream = await _client.Containers.GetContainerLogsAsync(
            _containerId,
            false,
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
    }

    public async Task CleanupAsync()
    {
        if (!string.IsNullOrEmpty(_containerId))
        {
            await _client.Containers.RemoveContainerAsync(_containerId, new ContainerRemoveParameters { Force = true });
            Console.WriteLine("[Docker] Kontener usunięty.");
            _containerId = null;
        }
    }

    public void Dispose()
    {
        _client.Dispose();
    }

}


