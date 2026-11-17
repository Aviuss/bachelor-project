using System;
using Shared.Contracts;

namespace Agent.Runner.CSharp.Implementations;

public class CopyConfigJob : CodeRunner
{
    public CopyConfigJob(Assignment assignment) : base(assignment) { }

    protected override async Task RunCodeAsync(string inputPath, string outputPath)
    {
        string config = await File.ReadAllTextAsync(Path.Combine(inputPath, "config.json"));
        await File.WriteAllTextAsync(Path.Combine(outputPath, "result.json"), config);
    }
}
