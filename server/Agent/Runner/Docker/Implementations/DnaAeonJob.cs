using System;
using Shared.Contracts;

namespace Agent.Runner.Docker.Implementations;

public enum DnaAeonMode
{
    Encode,
    Decode
}

public class DnaAeonJob : DockerRunner
{
    private DnaAeonMode _mode;

    public DnaAeonJob(Assignment assignment, DnaAeonMode mode) : base(assignment, "dna_aeon:v1")
    {
        _mode = mode;

        Entrypoint = ["/bin/bash"];
        switch (_mode)
        {
            case DnaAeonMode.Encode:
                ContainerInputDir = "/DNA_Aeon/data";
                ContainerOutputDir = null;
                Cmd = ["python3", "encode.py", "--config", "config.json"];
                break;
            case DnaAeonMode.Decode:
                ContainerInputDir = "/DNA_Aeon/data";
                ContainerOutputDir = null;
                Cmd = ["python3", "decode.py", "--config", "config.json"];
                break;
        }
    }

    protected override Task PostRun()
    {
        if (InputPath is null || OutputPath is null)
            throw new InvalidOperationException("Folders have not been prepared.");

        switch (_mode)
        {
            case DnaAeonMode.Encode:
                File.Move(Path.Combine(InputPath, "encoded.fasta"), Path.Combine(OutputPath, "encoded.fasta"));
                File.Move(Path.Combine(InputPath, "encoded.ini"), Path.Combine(OutputPath, "encoded.ini"));
                break;
            case DnaAeonMode.Decode:
                string decodedPath = Path.Combine(InputPath, "results", "D");
                string originalPath = Path.Combine(InputPath, "originalData");

                bool same = File.Exists(decodedPath)
                    && File.ReadAllBytes(decodedPath).AsSpan().SequenceEqual(File.ReadAllBytes(originalPath));

                File.WriteAllText(Path.Combine(OutputPath, "result"), same.ToString());
                break;
        }
            
        return Task.CompletedTask;
    }

}
