using AudioPilot.Cli;

namespace AudioPilot.CliHost
{
    internal static class CliDocsMaintenance
    {
        private const string CommandName = "internal-docs-sync";
        private const string WriteFlag = "--write";
        private const string CheckFlag = "--check";
        private const string RelativeCliGuidePath = "docs\\CLI.md";

        internal static bool TryHandle(string[] args, TextWriter standardOutput, TextWriter standardError, out int exitCode)
        {
            exitCode = 0;
            if (args.Length == 0 || !string.Equals(args[0], CommandName, StringComparison.Ordinal))
            {
                return false;
            }

            bool write = false;
            bool check = false;
            for (int index = 1; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case WriteFlag:
                        write = true;
                        break;
                    case CheckFlag:
                        check = true;
                        break;
                    default:
                        standardError.WriteLine($"Unknown maintenance flag '{args[index]}'. Use {WriteFlag} or {CheckFlag}.");
                        exitCode = 2;
                        return true;
                }
            }

            if (write == check)
            {
                standardError.WriteLine($"Specify exactly one of {WriteFlag} or {CheckFlag}.");
                exitCode = 2;
                return true;
            }

            string cliGuidePath = ResolveCliGuidePath();
            string markdown = File.ReadAllText(cliGuidePath);
            string syncedMarkdown = CliDocumentationSync.SyncCliGuide(markdown);

            if (write)
            {
                if (!string.Equals(markdown, syncedMarkdown, StringComparison.Ordinal))
                {
                    File.WriteAllText(cliGuidePath, syncedMarkdown);
                    standardOutput.WriteLine($"Updated {RelativeCliGuidePath}.");
                }
                else
                {
                    standardOutput.WriteLine($"No changes required for {RelativeCliGuidePath}.");
                }

                exitCode = 0;
                return true;
            }

            if (CliDocumentationSync.IsCliGuideInSync(markdown))
            {
                standardOutput.WriteLine($"{RelativeCliGuidePath} is in sync.");
                exitCode = 0;
                return true;
            }

            standardError.WriteLine($"{RelativeCliGuidePath} is out of sync. Run scripts/update-cli-docs.ps1 to refresh generated CLI docs blocks.");
            exitCode = 1;
            return true;
        }

        private static string ResolveCliGuidePath()
        {
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", RelativeCliGuidePath));
        }
    }
}
