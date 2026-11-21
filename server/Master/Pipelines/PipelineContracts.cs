using System.Text.Json;

namespace Master.Pipelines;

public record PipelineRequest(string PipelineType, string? Email, JsonElement Params);

public record UploadSlot(string Name, Guid ResourceId);

public record PipelineCreated(Guid RequestId, IReadOnlyList<UploadSlot> Uploads);
