using System;
using Shared.Contracts;

namespace Agent.Jobs.Implementation;

public class SimpleJob : AbstractJob
{
    public override int JobTypeId => 1;

    public SimpleJob(Assignment assignment) : base(assignment) { }

    public override void PreRun() { }

    public override void Run() { }

    public override void PostRun() { }
}
