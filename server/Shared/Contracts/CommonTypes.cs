using System.Text.Json.Serialization;

namespace Shared.Contracts;

public enum Direction
{
    Input,
    Output
}

public enum ResourceFormat
{
    JSON,
    TXT,
    BINARY
}

public enum ResourceLocation
{
    S3,
    DB
}


public abstract record Resource(Guid ResourceId, Direction Direction, ResourceFormat ResourceFormat, ResourceLocation ResourceLocation);


public record Assignment(int AttemptId, int JobTypeId, string ParamsJson, IReadOnlyCollection<Resource> Resources);

public record HeartbeatResponse(IReadOnlyList<Assignment> Assignments);