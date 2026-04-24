using AudioPilot.Cli;

namespace AudioPilot.Tests.Cli;

public sealed class CliDocumentationSyncTests
{
    [Fact]
    public void SyncCliGuide_PreservesManualQuickReferenceUseCasesAndCommandSelectorNotes()
    {
        string markdown = File.ReadAllText(Path.Combine(ResolveRepoRoot(), "docs", "CLI.md"));

        string synced = CliDocumentationSync.SyncCliGuide(markdown);

        Assert.Contains("Use cases:", synced, StringComparison.Ordinal);
        Assert.Contains("Selector notes:", synced, StringComparison.Ordinal);
        Assert.True(CliDocumentationSync.IsCliGuideInSync(synced));
    }

    [Fact]
    public void SyncCliGuide_ReplacesGeneratedSections()
    {
        string markdown = File.ReadAllText(Path.Combine(ResolveRepoRoot(), "docs", "CLI.md"));
        string mutated = markdown.Replace("AudioPilot.Cli.exe status --json", "AudioPilot.Cli.exe status", StringComparison.Ordinal);

        string synced = CliDocumentationSync.SyncCliGuide(mutated);

        Assert.Contains("AudioPilot.Cli.exe status --json", synced, StringComparison.Ordinal);
        Assert.True(CliDocumentationSync.IsCliGuideInSync(synced));
    }

    [Fact]
    public void SyncCliGuide_PreservesExactHelpNotesForSwitchCycleAndWait()
    {
        string markdown = File.ReadAllText(Path.Combine(ResolveRepoRoot(), "docs", "CLI.md"));

        string synced = CliDocumentationSync.SyncCliGuide(markdown);

        Assert.Contains("Input switching supports --reverse, --dry-run, and --require-current, but not --mute-mic, --mute-sound, or --deafen.", synced, StringComparison.Ordinal);
        Assert.Contains("cycle reorder expects the full current cycle device list in the new order; the parser rejects blank or duplicate ids, and execution verifies the configured cycle membership.", synced, StringComparison.Ordinal);
        Assert.Contains("Use --output or --input to scope the wait to one device class; the parser rejects passing both flags together, and omitting both lets either class satisfy the wait.", synced, StringComparison.Ordinal);
    }

    private static string ResolveRepoRoot()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }
}
