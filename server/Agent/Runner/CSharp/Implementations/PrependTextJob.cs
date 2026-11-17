using System;
using System.Text.Json;
using Shared.Contracts;

namespace Agent.Runner.CSharp.Implementations;

public class PrependTextJob : CodeRunner
{
    public PrependTextJob(Assignment assignment) : base(assignment) { }

    protected override async Task RunCodeAsync(string inputPath, string outputPath)
    {
        string text = await File.ReadAllTextAsync(Path.Combine(inputPath, "text.txt"));
        string prefix = JsonDocument.Parse(ParamsJson).RootElement.GetProperty("text").GetString()!;

        await File.WriteAllTextAsync(Path.Combine(outputPath, "text.txt"), prefix + text);
    }
}
