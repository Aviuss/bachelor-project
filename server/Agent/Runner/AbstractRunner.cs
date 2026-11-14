using System;
using Shared.Contracts;

namespace Agent.Runner;

public enum RunStatus
{
    Assigned,
    Running,
    Succeeded,
    Failed
}

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
    protected IReadOnlyCollection<Resource> OutputResources { get; }

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
        OutputResources = byDir[ResourceDirection.Output].ToList();
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


    protected virtual Task PreRun() => Task.CompletedTask;
    protected abstract Task Run();
    protected virtual Task PostRun() => Task.CompletedTask;

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

    public async Task ExecuteAsync()
    {
        if (Status != RunStatus.Assigned)
            throw new InvalidOperationException($"Attempt {AttemptId} has already been started.");

        Status = RunStatus.Running;
        StartedAt = DateTimeOffset.UtcNow;

        Exception? error = null;
        try
        {
            PrepareFolders();
            await PreRun();
            await Run();
            await PostRun();
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
