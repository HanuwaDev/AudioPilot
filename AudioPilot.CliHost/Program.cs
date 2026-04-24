namespace AudioPilot.CliHost
{
    internal static class Program
    {
        private const string ForceHeadlessFailureEnvVar = "AUDIOPILOT_TEST_FORCE_HEADLESS_FAILURE";

        private static int Main(string[] args)
        {
            if (CliDocsMaintenance.TryHandle(args, Console.Out, Console.Error, out int maintenanceExitCode))
            {
                return maintenanceExitCode;
            }

            return CliHostRuntime.Execute(
                args,
                new CliHostRuntimeDependencies(
                    () => new SingleInstanceHelperAdapter(),
                    () => new LocalHeadlessCommandRunnerAdapter(),
                    ShouldForceHeadlessFailure,
                    Console.Out,
                    Console.Error));
        }

        private static bool ShouldForceHeadlessFailure()
        {
            string? value = Environment.GetEnvironmentVariable(ForceHeadlessFailureEnvVar);
            return string.Equals(value, "1", StringComparison.Ordinal);
        }

    }
}
