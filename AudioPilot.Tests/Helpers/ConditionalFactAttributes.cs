namespace AudioPilot.Tests.Helpers;

internal sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (!TestExecutionGuards.ShouldRunIntegration())
        {
            Skip = TestExecutionGuards.GetIntegrationSkipReason();
        }
    }
}

internal sealed class StressFactAttribute : FactAttribute
{
    public StressFactAttribute()
    {
        if (!TestExecutionGuards.ShouldRunStress())
        {
            Skip = TestExecutionGuards.GetStressSkipReason();
        }
    }
}

internal sealed class VisualIntegrationFactAttribute : FactAttribute
{
    public VisualIntegrationFactAttribute()
    {
        if (!TestExecutionGuards.ShouldRunVisualWpfIntegration())
        {
            Skip = TestExecutionGuards.GetVisualWpfSkipReason();
        }
    }
}
