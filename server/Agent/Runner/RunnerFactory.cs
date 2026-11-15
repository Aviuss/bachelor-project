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
    private readonly ConcurrentDictionary<(int JobId, int AttemptId), string> _rejected = new();
    public IReadOnlyDictionary<(int JobId, int AttemptId), AbstractRunner> Runs => _runs;

    public RunnerFactory(S3manager s3)
    {
        _s3 = s3;
    }

    public async Task RunAsync(Assignment assignment)
    {
        var key = (assignment.JobId, assignment.AttemptId);
        if (_runs.ContainsKey(key) || _rejected.ContainsKey(key))
            return;

        if (!RunnerDefinitions.TryGetValue(assignment.JobTypeId, out var create))
        {
            _rejected.TryAdd(key, $"Not found jobTypeId {assignment.JobTypeId}");
            return;
        }

        AbstractRunner runner;
        try
        {
            runner = create(assignment);
        }
        catch (Exception ex)
        {
            _rejected.TryAdd(key, ex.Message);
            return;
        }

        using (runner)
        {
            if (!_runs.TryAdd(key, runner))
                return;

            await runner.ExecuteAsync(_s3);
        }
    }

    public IReadOnlyList<AttemptReport> GetReports()
    {
        var reports = new List<AttemptReport>();

        foreach (var (key, runner) in _runs)
        {
            var outputs = runner.Status == RunStatus.Succeeded ? runner.OutputResources : [];
            
            reports.Add(new AttemptReport(key.JobId, key.AttemptId, runner.Status, runner.Error?.Message, runner.StartedAt, runner.FinishedAt, outputs));
        }

        foreach (var (key, error) in _rejected)
            reports.Add(new AttemptReport(key.JobId, key.AttemptId, RunStatus.Failed, error, null, null, []));

        return reports;
    }

    public void Acknowledge((int JobId, int AttemptId) key)
    {
        if (_rejected.TryRemove(key, out _))
            return;

        if (!_runs.TryGetValue(key, out var runner))
            return;

        runner.Cleanup();
        _runs.TryRemove(key, out _);
    }
}
