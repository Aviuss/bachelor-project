using System;
using System.Globalization;
using System.Text.Json;
using Shared;
using Shared.Contracts;

namespace Agent.Runner;

public abstract class AbstractRunner : IDisposable
{
    public RunStatus Status { get; private set; } = RunStatus.Assigned;
    public bool IsFinished => Status is RunStatus.Succeeded or RunStatus.Failed;
    public Exception? Error { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }

    protected int JobId { get; }
    protected int AttemptId { get; }
    protected int JobTypeId { get; }
    protected string ParamsJson { get; }
    protected IReadOnlyCollection<Resource> InputResources { get; }

    private readonly List<Resource> _outputResources;
    public IReadOnlyList<Resource> OutputResources => _outputResources;

    protected string? InputPath { get; private set; }
    protected string? OutputPath { get; private set; }

    protected AbstractRunner(Assignment assignment)
    {
        JobId = assignment.JobId;
        AttemptId = assignment.AttemptId;
        JobTypeId = assignment.JobTypeId;
        ParamsJson = assignment.ParamsJson;

        var byDir = assignment.Resources.ToLookup(r => r.Direction);
        InputResources = byDir[ResourceDirection.Input].ToList();
        _outputResources = byDir[ResourceDirection.Output].ToList();
    }

    private string JobFolder => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", JobId.ToString());
    private string AttemptFolder => Path.Combine(JobFolder, AttemptId.ToString());

    private void PrepareFolders()
    {
        string inputFolder = Path.Combine(AttemptFolder, "input");
        string outputFolder = Path.Combine(AttemptFolder, "output");

        Directory.CreateDirectory(inputFolder);
        Directory.CreateDirectory(outputFolder);

        InputPath = inputFolder;
        OutputPath = outputFolder;

        File.WriteAllText(Path.Combine(inputFolder, "config.json"), ParamsJson);
    }


    protected virtual bool UseFilesForDbResources => true;

    protected virtual Task PreRun() => Task.CompletedTask;
    protected abstract Task Run();
    protected virtual Task PostRun() => Task.CompletedTask;

    protected static string FileName(Resource resource) {
        string format = resource.Format switch
        {
            ResourceFormat.JSON => ".json",
            ResourceFormat.TXT or ResourceFormat.INT or ResourceFormat.FLOAT or ResourceFormat.DOUBLE or ResourceFormat.BOOL => ".txt",
            ResourceFormat.BINARY => ".bin",
            _ => throw new NotSupportedException($"Resource format {resource.Format} is not supported."),
        };

        return resource.Name + format;
    }

    private static string ToFileContent(DbResource resource) => resource.Format switch
    {
        ResourceFormat.JSON => resource.GetJson().GetRawText(),
        ResourceFormat.INT => resource.GetInt().ToString(CultureInfo.InvariantCulture),
        ResourceFormat.FLOAT => resource.GetFloat().ToString(CultureInfo.InvariantCulture),
        ResourceFormat.DOUBLE => resource.GetDouble().ToString(CultureInfo.InvariantCulture),
        ResourceFormat.BOOL => resource.GetBool() ? "true" : "false",
        _ => throw new NotSupportedException($"DB resource format {resource.Format} is not supported."),
    };

    private async Task DownloadResource(Resource resource, S3manager s3, string inputPath)
    {
        string fileName = FileName(resource);

        switch (resource)
        {
            case S3Resource s3Resource:
                await s3.DownloadResource(s3Resource.GetKey(), inputPath, fileName);
                break;
            case DbResource db when UseFilesForDbResources:
                await File.WriteAllTextAsync(Path.Combine(inputPath, fileName), ToFileContent(db));
                break;
        }
    }

    private async Task PrepareInputs(S3manager s3)
    {
        PrepareFolders();
        await Task.WhenAll(InputResources.Select(r => DownloadResource(r, s3, InputPath!)));
    }

    protected void SetOutputValue(string name, JsonElement value) => SetOutput(name, ResourceFormat.JSON, value);
    protected void SetOutputValue(string name, int value) => SetOutput(name, ResourceFormat.INT, value);
    protected void SetOutputValue(string name, float value) => SetOutput(name, ResourceFormat.FLOAT, value);
    protected void SetOutputValue(string name, double value) => SetOutput(name, ResourceFormat.DOUBLE, value);
    protected void SetOutputValue(string name, bool value) => SetOutput(name, ResourceFormat.BOOL, value);

    private void SetOutput<T>(string name, ResourceFormat format, T value)
    {
        int index = _outputResources.FindIndex(r => r is DbResource && r.Name == name);
        if (index < 0)
            throw new ArgumentException($"There is no DB output named '{name}'.", nameof(name));

        var resource = (DbResource)_outputResources[index];
        if (resource.Format != format)
            throw new InvalidOperationException($"Output resource '{name}' has format {resource.Format}, not {format}.");

        _outputResources[index] = resource with { Value = JsonSerializer.SerializeToElement(new { value }) };
    }

    private async Task UploadOutputs(S3manager s3)
    {
        await CollectDbOutputs(OutputPath!);
        await Task.WhenAll(_outputResources.OfType<S3Resource>().Select(r => UploadResource(r, s3, OutputPath!, AttemptId)));
    }

    private async Task CollectDbOutputs(string outputPath)
    {
        for (int i = 0; i < _outputResources.Count; i++)
        {
            var resource = _outputResources[i];

            if (resource is DbResource)
            {
                var dbResource = (DbResource)resource;

                if (UseFilesForDbResources)
                    _outputResources[i] = await ReadDbOutput(dbResource, outputPath);
                else
                {
                    bool hasValue = dbResource.Value.ValueKind == JsonValueKind.Object
                                    && dbResource.Value.TryGetProperty("value", out _);

                    if (!hasValue)
                        throw new InvalidOperationException($"Output resource '{dbResource.Name}' has no value.");
                }
            }
        }
    }

    private static async Task<DbResource> ReadDbOutput(DbResource resource, string outputPath)
    {
        string fileName = FileName(resource);
        string filePath = Path.Combine(outputPath, fileName);

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Output resource '{resource.Name}' was not produced.", fileName);

        string content = await File.ReadAllTextAsync(filePath);

        object value = resource.Format switch
        {
            ResourceFormat.JSON => JsonSerializer.Deserialize<JsonElement>(content),
            ResourceFormat.INT => int.Parse(content.Trim(), CultureInfo.InvariantCulture),
            ResourceFormat.FLOAT => float.Parse(content.Trim(), CultureInfo.InvariantCulture),
            ResourceFormat.DOUBLE => double.Parse(content.Trim(), CultureInfo.InvariantCulture),
            ResourceFormat.BOOL => bool.Parse(content.Trim()),
            _ => throw new NotSupportedException($"DB resource format {resource.Format} is not supported."),
        };

        return resource with { Value = JsonSerializer.SerializeToElement(new { value }) };
    }

    private static async Task UploadResource(S3Resource resource, S3manager s3, string outputPath, int attemptId)
    {
        string fileName = FileName(resource);

        if (!File.Exists(Path.Combine(outputPath, fileName)))
            throw new FileNotFoundException($"Output resource '{resource.Name}' was not produced.", fileName);

        await s3.WriteResource(resource.GetKey(attemptId), outputPath, fileName);
    }

    public void Cleanup()
    {
        if (!IsFinished)
            throw new InvalidOperationException($"Attempt {AttemptId} is not finished yet.");

        if (Directory.Exists(AttemptFolder))
            Directory.Delete(AttemptFolder, recursive: true);

        if (Directory.Exists(JobFolder) && !Directory.EnumerateFileSystemEntries(JobFolder).Any())
            Directory.Delete(JobFolder);

        InputPath = null;
        OutputPath = null;
    }

    public async Task ExecuteAsync(S3manager s3)
    {
        if (Status != RunStatus.Assigned)
            throw new InvalidOperationException($"Attempt {AttemptId} has already been started.");

        Status = RunStatus.Running;
        StartedAt = DateTimeOffset.UtcNow;
        await Task.Yield();

        Exception? error = null;
        try
        {
            await PrepareInputs(s3);
            await PreRun();
            await Run();
            await PostRun();
            await UploadOutputs(s3);
        }
        catch (Exception ex)
        {
            error = ex;
            Console.WriteLine($"[Runner] Próba {AttemptId} nie powiodła się: {ex.Message}");
        }

        Error = error;
        FinishedAt = DateTimeOffset.UtcNow;
        Status = error is null ? RunStatus.Succeeded : RunStatus.Failed;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing) { }
}
