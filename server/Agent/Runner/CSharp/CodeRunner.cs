using System;
using Shared.Contracts;

namespace Agent.Runner.CSharp;

public abstract class CodeRunner : AbstractRunner
{
    protected CodeRunner(Assignment assignment) : base(assignment) { }

    protected abstract Task RunCodeAsync(string inputPath, string outputPath);

    protected override async Task Run()
    {
        if (InputPath is null || OutputPath is null)
            throw new InvalidOperationException("Folders have not been prepared.");

        Console.WriteLine($"[Code] Uruchamianie próby {AttemptId}.");
        await RunCodeAsync(InputPath, OutputPath);
        Console.WriteLine($"[Code] Próba {AttemptId} zakończona.");
    }
}
