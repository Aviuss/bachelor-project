using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shared.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<ResourceDirection>))]
public enum ResourceDirection
{
    Input,
    Output
}

[JsonConverter(typeof(JsonStringEnumConverter<ResourceFormat>))]
public enum ResourceFormat
{
    JSON,
    TXT,
    BINARY,
    INT,
    FLOAT,
    DOUBLE,
    BOOL,
    FASTA,
    NONE
}

[JsonConverter(typeof(JsonStringEnumConverter<ResourceLocation>))]
public enum ResourceLocation
{
    S3,
    DB
}


[JsonPolymorphic(TypeDiscriminatorPropertyName = "location")]
[JsonDerivedType(typeof(S3Resource), "S3")]
[JsonDerivedType(typeof(DbResource), "DB")]
public abstract record Resource(Guid ResourceId, ResourceDirection Direction, string Name, ResourceFormat Format);

public record S3Resource(Guid ResourceId, ResourceDirection Direction, string Name, ResourceFormat Format, int? ProducedBy)
    : Resource(ResourceId, Direction, Name, Format)
{
    public string GetKey(int? attemptId = null) => (Direction, ProducedBy, attemptId) switch
    {
        (ResourceDirection.Input, int producer, null) => $"{ResourceId}/{producer}",
        (ResourceDirection.Input, null, null) => $"{ResourceId}/upload",
        (ResourceDirection.Output, null, int attempt) => $"{ResourceId}/{attempt}",
        _ => throw new ArgumentException(
            $"Invalid key request for {Direction} resource '{Name}' (producedBy={ProducedBy}, attemptId={attemptId}).",
            nameof(attemptId)),
    };
}

public record DbResource(Guid ResourceId, ResourceDirection Direction, string Name, ResourceFormat Format, JsonElement Value)
    : Resource(ResourceId, Direction, Name, Format)
{
    public JsonElement GetJson() => Get(ResourceFormat.JSON);
    public int GetInt() => Get(ResourceFormat.INT).GetInt32();
    public float GetFloat() => Get(ResourceFormat.FLOAT).GetSingle();
    public double GetDouble() => Get(ResourceFormat.DOUBLE).GetDouble();
    public bool GetBool() => Get(ResourceFormat.BOOL).GetBoolean();

    private JsonElement Get(ResourceFormat expected)
    {
        if (Format != expected)
            throw new InvalidOperationException($"Resource '{Name}' has format {Format}, not {expected}.");

        if (Value.ValueKind != JsonValueKind.Object || !Value.TryGetProperty("value", out var value))
            throw new InvalidOperationException($"Resource '{Name}' has no value.");

        return value;
    }
}


[JsonConverter(typeof(JsonStringEnumConverter<RunStatus>))]
public enum RunStatus
{
    Assigned,
    Running,
    Succeeded,
    Failed
}

public static class JobTypes
{
    public const int AppendText = 311;
    public const int PrependText = 411;
    public const int DnaAeonEncode = 1;
    public const int DnaAeonDecode = 2;
}


public record Assignment(int JobId, int AttemptId, int JobTypeId, string ParamsJson, IReadOnlyCollection<Resource> Resources);

public record AttemptReport(int JobId, int AttemptId, RunStatus Status, string? Error, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, IReadOnlyList<Resource> Outputs);

public record HeartbeatRequest(IReadOnlyList<AttemptReport> Reports);

public record AttemptKey(int JobId, int AttemptId);

public record HeartbeatResponse(IReadOnlyList<Assignment> Assignments, IReadOnlyList<AttemptKey> Acknowledged, IReadOnlyList<AttemptKey> ToCancel);
