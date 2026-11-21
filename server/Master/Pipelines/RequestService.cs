using System.Security.Cryptography;
using Dapper;
using Npgsql;
using Shared;
using Shared.Contracts;

namespace Master.Pipelines;

public static class RequestService
{
    public static async Task<IResult> CreateAsync(PipelineRequest request, NpgsqlDataSource db)
    {
        try
        {
            return Results.Ok(await PipelineFactory.CreateAsync(db, request));
        }
        catch (Exception e) when (e is ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            return Results.BadRequest(new { error = e.Message });
        }
    }

    public static async Task<IResult> UploadAsync(
        Guid requestId, Guid resourceId, string? fileName, HttpRequest http, NpgsqlDataSource db, S3manager s3)
    {
        await using var connection = await db.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var slot = await connection.QuerySingleOrDefaultAsync<Guid?>(
            """
            SELECT upload.id
            FROM resources AS upload
            JOIN job_inputs AS used_by ON used_by.resource_id = upload.id
            JOIN jobs AS job ON job.id = used_by.job_id
            JOIN job_type_io AS io ON io.id = used_by.job_type_io_id
            WHERE upload.id = @resourceId
              AND job.request_id = @requestId
              AND upload.producer_job_id IS NULL
              AND upload.status = 'pending'
              AND io.location_id = 's3'
            LIMIT 1
            FOR UPDATE OF upload
            """,
            new { requestId, resourceId }, transaction);

        if (slot is null)
            return Results.NotFound(new { error = $"Request {requestId} has no pending upload {resourceId}." });

        var tmpFolder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string filePath = Path.Combine(tmpFolder, "upload");
            await using var file = File.Create(filePath);
            await http.Body.CopyToAsync(file);

            file.Position = 0;
            byte[] sha256 = await SHA256.HashDataAsync(file);
            long size = file.Length;
            await file.DisposeAsync();

            string key = new S3Resource(resourceId, ResourceDirection.Input, "", ResourceFormat.BINARY, null).GetKey();
            await s3.WriteResource(key, tmpFolder, "upload");

            await connection.ExecuteAsync(
                """
                UPDATE resources
                SET status = 'available', original_name = @fileName, size_bytes = @size, sha256 = @sha256
                WHERE id = @resourceId
                """,
                new { resourceId, fileName, size, sha256 }, transaction);

            await JobReadiness.UpdateAsync(connection, transaction);
            await transaction.CommitAsync();
        }
        finally
        {
            Directory.Delete(tmpFolder, recursive: true);
        }

        return Results.NoContent();
    }
}
