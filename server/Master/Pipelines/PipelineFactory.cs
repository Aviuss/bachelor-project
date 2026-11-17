using Dapper;
using Master.Pipelines.Implementations;
using Npgsql;

namespace Master.Pipelines;

public static class PipelineFactory
{
    private static readonly Dictionary<string, Func<AbstractPipeline>> PipelineDefinitions = new()
    { // pipeline_types.name : creates AbstractPipeline
        ["text-wrap"] = () => new TextWrapPipeline(),
    };

    public static async Task<PipelineCreated> CreateAsync(NpgsqlDataSource db, PipelineRequest request)
    {
        if (!PipelineDefinitions.TryGetValue(request.PipelineType, out var create))
            throw new ArgumentException($"Unknown pipeline type '{request.PipelineType}'.");

        await using var connection = await db.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        int pipelineTypeId = await connection.QuerySingleOrDefaultAsync<int?>(
            "SELECT id FROM pipeline_types WHERE name = @PipelineType",
            new { request.PipelineType }, transaction)
            ?? throw new ArgumentException($"Pipeline type '{request.PipelineType}' is not in the database.");

        var created = await create().InstantiateAsync(connection, transaction, pipelineTypeId, request.Email, request.Params);

        await transaction.CommitAsync();
        return created;
    }
}
