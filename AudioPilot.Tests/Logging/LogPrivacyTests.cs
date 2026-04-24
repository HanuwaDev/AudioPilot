using AudioPilot.Logging;
using AudioPilot.Models;

namespace AudioPilot.Tests.Logging;

[CollectionDefinition("LogPrivacy", DisableParallelization = true)]
public sealed class LogPrivacyTestCollection;

[Collection("LogPrivacy")]
public sealed class LogPrivacyTests
{
    [Fact]
    public void Label_RedactsByDefault()
    {
        LogPrivacy.ApplySettings(new Settings { Miscellaneous = new MiscellaneousSettings { RedactLogContent = true } });

        string result = LogPrivacy.Label("Desk Speakers");

        Assert.StartsWith("len=", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Desk Speakers", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Label_ReturnsRawValue_WhenRedactionDisabled()
    {
        LogPrivacy.ApplySettings(new Settings { Miscellaneous = new MiscellaneousSettings { RedactLogContent = false } });

        string result = LogPrivacy.Label("Desk Speakers");

        Assert.Equal("Desk Speakers", result);
    }

    [Fact]
    public void ApplySettings_Null_ResetsToPrivacyFirstDefault()
    {
        LogPrivacy.ApplySettings(new Settings { Miscellaneous = new MiscellaneousSettings { RedactLogContent = false } });
        LogPrivacy.ApplySettings(null);

        Assert.True(LogPrivacy.IsRedactionEnabled);
    }
}
