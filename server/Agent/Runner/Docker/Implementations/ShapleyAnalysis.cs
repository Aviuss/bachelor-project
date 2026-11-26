using System;
using Shared.Contracts;
using System.Text.Json;


namespace Agent.Runner.Docker.Implementations;


public class ShapleyAnalysis : DockerRunner
{
    protected override bool UseFilesForDbResources => false;

    public ShapleyAnalysis(Assignment assignment) : base(assignment, "shapleyanalysis:v1")
    {
        ContainerInputDir = "indir";
        ContainerOutputDir = "outdir";//null;
        Cmd = ["cmd stuff"];
    }

    protected override Task PreRun(CancellationToken cancellationToken)
    {
        List<JsonElement> decodingResults = InputResources.OfType<DbResource>().Select(e => e.GetJson()).ToList();
        // { "codec": [STRING], "synthesis": [1/0], "sequencing": [1/0], "pcr": [1/0], "decay": [?NWM JAKI FORMAT], "result": [0-1] (zero -> not decoded, one -> decoded, in between -> decoded partially)

        // here processing

        // save .csv in:
        // InputPath

        return Task.CompletedTask;
    }

}
