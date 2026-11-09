using System;
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
        using var runner = new Runner("alpine:3.20", "./input");
        //runner.PrepareTestData("Witaj z zamontowanego folderu!");
        //await runner.PrintImages();
        await runner.RunByImageNameAsync("dt4dds-custom");
       // await runner.CreateContainerWithMountAsync();
       // await runner.RunAsync();
        await runner.VerifyLogsAsync();
        await runner.CleanupAsync();
    }
}

class Runner : IDisposable
{
    private readonly DockerClient _client;
    private string? _containerId;

    public string ImageName { get; set; }
    public string InputPath { get; set; }

    public Runner(string imageName = "alpine:3.20", string inputPath = "./input")
    {
        ImageName = imageName;
        InputPath = inputPath;

        var uri = OperatingSystem.IsWindows()
            ? new Uri("npipe://./pipe/docker_engine")
            : new Uri("unix:///var/run/docker.sock");

        _client = new DockerClientConfiguration(uri).CreateClient();
    }


    public async Task PrintImages()
    {
        IList<ImagesListResponse> images = await _client.Images.ListImagesAsync(
            new ImagesListParameters
            {
                All = true // true uwzględnia również obrazy pośrednie/dangling
            });

        foreach (var image in images)
        {

            if (image.RepoTags != null && image.RepoTags.Count > 0)
            {
                Console.WriteLine("Tagi: " + string.Join(", ", image.RepoTags));
            }
            else
            {
                Console.WriteLine("Tagi: <brak tagu / dangling>");
            }

            Console.WriteLine(new string('-', 40));
        }
    }
  
    public void PrepareTestData(string content)
    {
        Directory.CreateDirectory(InputPath);
        File.WriteAllText(Path.Combine(InputPath, "test.txt"), content);
        Console.WriteLine($"[Host] Zapisano plik testowy w: {Path.GetFullPath(InputPath)}");
    }

    public async Task CreateContainerWithMountAsync()
    {
        string absoluteHostPath = Path.GetFullPath(InputPath).Replace("\\", "/");
        const string containerTargetDir = "/app/input";
        string bindConfig = $"{absoluteHostPath}:{containerTargetDir}:ro";
        

        Console.WriteLine($"[Docker] Tworzenie kontenera z montowaniem: {bindConfig}");

        await _client.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = ImageName },
            null,
            new Progress<JSONMessage>(m =>
            {
                if (!string.IsNullOrEmpty(m.Status)) Console.WriteLine($"[Pull] {m.Status}");
            })
        );

        var response = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = ImageName,
            Cmd = new[] { "cat", $"{containerTargetDir}/test.txt" },
            HostConfig = new HostConfig
            {
                Binds = new[] { bindConfig }
            }
        });

        _containerId = response.ID;
    }

    public async Task RunAsync()
    {
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


    public async Task RunByImageNameAsync(string targetImageName)
    {
        // 1. Pobierz listę lokalnych obrazów
        var images = await _client.Images.ListImagesAsync(new ImagesListParameters { All = false });

        // 2. Sprawdź, czy targetImageName znajduje się na liście tagów
        // Jeśli użytkownik podał np. "alpine", Docker traktuje to jako "alpine:latest"
        string normalizedTarget = targetImageName.Contains(':') 
            ? targetImageName 
            : $"{targetImageName}:latest";

        bool existsLocally = images.Any(img => 
            img.RepoTags != null && 
            img.RepoTags.Contains(normalizedTarget, StringComparer.OrdinalIgnoreCase));

        // 3. Jeśli nie ma lokalnie – pobierz z rejestru
        if (!existsLocally)
        {
            Console.WriteLine($"[Docker] Obraz '{normalizedTarget}' nie istnieje lokalnie. Pobieranie...");
            
            await _client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = normalizedTarget },
                null,
                new Progress<JSONMessage>(m =>
                {
                    if (!string.IsNullOrEmpty(m.Status))
                        Console.WriteLine($"[Pull] {m.Status}");
                })
            );
        }
        else
        {
            Console.WriteLine($"[Docker] Obraz '{normalizedTarget}' znaleziony lokalnie.");
        }

        // 4. Utwórz i uruchom kontener
        var createResponse = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = normalizedTarget,
            Cmd = new[] { "echo", $"Wystartowano z obrazu: {normalizedTarget}" }
        });

        _containerId = createResponse.ID;

        await _client.Containers.StartContainerAsync(_containerId, new ContainerStartParameters());
        Console.WriteLine($"[Docker] Kontener {_containerId[..12]} wystartował pomyślnie.");
    }
}


