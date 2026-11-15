using System;
using System.Collections.Concurrent;
using Agent.Runner.CSharp.Implementations;
using Agent.Runner.Docker;
using Shared;
using Shared.Contracts;

namespace Agent.Runner;

public class RunnerFactory
{
    private static readonly Dictionary<int, Func<Assignment, AbstractRunner>> RunnerDefinitions = new()
    { // JobTypeId : creates AbstractRunner
        [1] = a => new DockerRunner(a, "alpine:3.20"),
        [2] = a => new CopyConfigJob(a),
    };

    private readonly S3manager _s3;
    private readonly ConcurrentDictionary<(int JobId, int AttemptId), AbstractRunner> _runs = new();
    public IReadOnlyDictionary<(int JobId, int AttemptId), AbstractRunner> Runs => _runs;

    public RunnerFactory(S3manager s3)
    {
        _s3 = s3;
    }

    public async Task RunAsync(Assignment assignment)
    {
        using var runner = Create(assignment);

        if (!_runs.TryAdd((assignment.JobId, assignment.AttemptId), runner))
            throw new InvalidOperationException($"Attempt {assignment.AttemptId} of job {assignment.JobId} is already registered.");

        // TODO: what if same jobid and different assignmentid?

        await runner.ExecuteAsync(_s3);
    }

    public void Acknowledge((int JobId, int AttemptId) key)
    {
        if (!_runs.TryGetValue(key, out var runner))
            throw new KeyNotFoundException($"Attempt {key.AttemptId} of job {key.JobId} is not registered.");

        runner.Cleanup();
        _runs.TryRemove(key, out _);
    }

    private static AbstractRunner Create(Assignment assignment) =>
        RunnerDefinitions.TryGetValue(assignment.JobTypeId, out var create)
            ? create(assignment)
            : throw new ArgumentException($"Not found jobTypeId {assignment.JobTypeId}", nameof(assignment));
}
