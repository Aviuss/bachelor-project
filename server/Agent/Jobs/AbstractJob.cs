using System;
using System.Text.Json;
using Shared.Contracts;

namespace Agent.Jobs;

public abstract class AbstractJob
{
    public abstract int JobTypeId { get; }
    public int AttemptId { get; }
    public string ParamsJson { get; }
    public IReadOnlyCollection<Resource> InputResources { get; }
    public IReadOnlyCollection<Resource> OutputResources { get; }

    public AbstractJob(Assignment assignment)
    {
        if (assignment.JobTypeId != this.JobTypeId)
        {
            throw new Exception("This assignment cannot be assigned to this job class");
        }

        this.AttemptId = assignment.AttemptId;
        this.ParamsJson = assignment.ParamsJson;
        
        var byDir = assignment.Resources.ToLookup(r => r.Direction);
        this.InputResources = byDir[Direction.Input].ToList();
        this.OutputResources = byDir[Direction.Output].ToList();
    }

    public abstract void PreRun();
    public abstract void Run();
    public abstract void PostRun();

}
