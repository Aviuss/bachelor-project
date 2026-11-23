using System;
using System.Collections.Concurrent;
using Agent.Runner.CSharp.Implementations;
using Agent.Runner.Docker.Implementations;
using Shared;
using Shared.Contracts;

namespace Agent.Runner;

public class RunnerFactory
{
    private static readonly Dictionary<int, Func<Assignment, AbstractRunner>> RunnerDefinitions = new()
    { // JobTypeId : creates AbstractRunner
        [JobTypes.AppendText] = a => new AppendTextJob(a),
        [JobTypes.PrependText] = a => new PrependTextJob(a),
        [JobTypes.DnaAeonEncode] = a => new DnaAeonJob(a, DnaAeonMode.Encode),
        [JobTypes.DnaAeonDecode] = a => new DnaAeonJob(a, DnaAeonMode.Decode),
    };

    private sealed record Run(AbstractRunner? Runner, Task? Execution, string? RejectedError);

    private readonly S3manager _s3;
    private readonly ConcurrentDictionary<(int JobId, int AttemptId), Run> _runs = new();

    public RunnerFactory(S3manager s3)
    {
        _s3 = s3;
    }

    public async Task RunAsync(Assignment assignment)
    {
        var key = (assignment.JobId, assignment.AttemptId);
        if (_runs.ContainsKey(key))
            return;

        if (!RunnerDefinitions.TryGetValue(assignment.JobTypeId, out var create))
        {
            _runs.TryAdd(key, new Run(null, null, $"Not found jobTypeId {assignment.JobTypeId}"));
            return;
        }

        AbstractRunner runner;
        try
        {
            runner = create(assignment);
        }
        catch (Exception ex)
        {
            _runs.TryAdd(key, new Run(null, null, ex.Message));
            return;
        }

        using (runner)
        {
            var execution = runner.ExecuteAsync(_s3);
            _runs[key] = new Run(runner, execution, null);
            await execution;
        }
    }

    public IReadOnlyList<AttemptReport> GetReports()
    {
        var reports = new List<AttemptReport>();

        foreach (var (key, run) in _runs)
        {
            var runner = run.Runner;
            if (runner is null)
            {
                reports.Add(new AttemptReport(key.JobId, key.AttemptId, RunStatus.Failed, run.RejectedError, null, null, []));
                continue;
            }

            reports.Add(new AttemptReport(key.JobId, key.AttemptId, runner.Status, runner.Error?.Message, runner.StartedAt, runner.FinishedAt, runner.OutputResources));
        }

        return reports;
    }

    public async Task CloseAsync((int JobId, int AttemptId) key)
    {
        if (!_runs.TryRemove(key, out var run) || run.Runner is null)
            return;

        try
        {
            run.Runner.Cancel();
            if (run.Execution is not null)
                await run.Execution;
            run.Runner.Cleanup();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Runner] Cleanup of attempt {key.AttemptId} failed: {e.Message}");
        }
    }
}
