using Master.Agent;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace Master.Pipelines;

public abstract class AbstractPipeline
{
    private NpgsqlConnection _connection = null!;
    private NpgsqlTransaction _transaction = null!;
    private Guid _requestId;
    private readonly List<UploadSlot> _uploads = [];

    public abstract string Version { get; }

    protected abstract Task BuildAsync(JsonElement parameters);

    public async Task<PipelineCreated> InstantiateAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, int pipelineTypeId, string? email, JsonElement parameters)
    {
        _connection = connection;
        _transaction = transaction;

        _requestId = await connection.QuerySingleAsync<Guid>(
            "INSERT INTO requests (pipeline_type_id, version, email) VALUES (@pipelineTypeId, @Version, @email) RETURNING id",
            new { pipelineTypeId, Version, email }, transaction);

        await BuildAsync(parameters);
        await JobReadiness.UpdateAsync(connection, transaction);

        return new PipelineCreated(_requestId, _uploads);
    }

    protected async Task<int> AddJobAsync(string name, int jobTypeId, JsonElement parameters)
    {
        int jobId = await _connection.QuerySingleAsync<int>(
            """
            INSERT INTO jobs (request_id, name, job_type_id, params)
            VALUES (@RequestId, @name, @jobTypeId, @Params::jsonb)
            RETURNING id
            """,
            new { RequestId = _requestId, name, jobTypeId, Params = parameters.GetRawText() }, _transaction);

        await _connection.ExecuteAsync(
            """
            INSERT INTO resources (producer_job_id, producer_io_id)
            SELECT @jobId, id FROM job_type_io
            WHERE job_type_id = @jobTypeId AND direction = 'out'
            """,
            new { jobId, jobTypeId }, _transaction);

        return jobId;
    }

    protected async Task<Guid> AddUploadAsync(string name)
    {
        var resourceId = await _connection.QuerySingleAsync<Guid>(
            "INSERT INTO resources DEFAULT VALUES RETURNING id", transaction: _transaction);

        _uploads.Add(new UploadSlot(name, resourceId));
        return resourceId;
    }

    protected async Task<Guid> OutputOfAsync(int jobId, string outputName) =>
        await _connection.QuerySingleOrDefaultAsync<Guid?>(
            """
            SELECT r.id FROM resources r
            JOIN job_type_io io ON io.id = r.producer_io_id
            WHERE r.producer_job_id = @jobId AND io.name = @outputName
            """,
            new { jobId, outputName }, _transaction)
        ?? throw new InvalidOperationException($"Job {jobId} has no output named '{outputName}'.");

    protected async Task LinkInputAsync(int jobId, string inputName, Guid resourceId)
    {
        int linked = await _connection.ExecuteAsync(
            """
            INSERT INTO job_inputs (job_id, job_type_io_id, resource_id)
            SELECT job.id, io.id, @resourceId
            FROM jobs AS job
            JOIN job_type_io AS io ON io.job_type_id = job.job_type_id
            WHERE job.id = @jobId
              AND io.direction = 'in'
              AND io.name = @inputName
            """,
            new { jobId, inputName, resourceId }, _transaction);

        if (linked != 1)
            throw new InvalidOperationException($"Job {jobId} has no input named '{inputName}'.");
    }
}
