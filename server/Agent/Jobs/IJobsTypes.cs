namespace Agent.Jobs;

public interface IJobsTypes
{
    int Id { get; }
    void PreRun();
    void Run();
    void PostRun();
}
