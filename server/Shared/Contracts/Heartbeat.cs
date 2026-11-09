namespace Shared.Contracts;

public record HeartbeatResponse(IReadOnlyList<Assignment> Assignments);

public record Assignment(int AttemptId, string JobType, int MemMb, decimal Cpu, int TimeoutS, string ParamsJson);
