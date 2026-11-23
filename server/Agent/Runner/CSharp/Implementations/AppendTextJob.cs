using System;
using System.Text.Json;
using Shared.Contracts;

namespace Agent.Runner.CSharp.Implementations;

public class AppendTextJob : CodeRunner
{
    public AppendTextJob(Assignment assignment) : base(assignment) { }

    protected override async Task RunCodeAsync(string inputPath, string outputPath, CancellationToken cancellationToken)
    {
        string text = await File.ReadAllTextAsync(Path.Combine(inputPath, "text.txt"), cancellationToken);
        string suffix = JsonDocument.Parse(ParamsJson).RootElement.GetProperty("text").GetString()!;

        await File.WriteAllTextAsync(Path.Combine(outputPath, "text.txt"), text + suffix, cancellationToken);
    }
}
