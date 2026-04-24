using System.Windows;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Services.UI;

[Collection("MessageBoxServiceIsolation")]
public sealed class MessageBoxServiceTests
{
    [Theory]
    [InlineData(MessageBoxButton.OK, true, true)]
    [InlineData(MessageBoxButton.OK, false, false)]
    [InlineData(MessageBoxButton.YesNo, true, false)]
    public void ShouldUpdateExistingWindow_ReturnsExpected(MessageBoxButton buttons, bool existingUpdated, bool expected)
    {
        bool result = MessageBoxService.ShouldUpdateExistingWindow(buttons, existingUpdated);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Show_UsesExistingWindow_ForOkDialogs()
    {
        var native = new RecordingMessageBoxNative { HasDialog = true };
        MessageBoxService.SetNativeForTests(native);

        try
        {
            MessageBoxResult result = MessageBoxService.Show("test", "caption", MessageBoxButton.OK, MessageBoxImage.Information);

            Assert.Equal(MessageBoxResult.OK, result);
            Assert.Equal(0, native.ShowCallCount);
            Assert.True(native.FindWindowCallCount > 0);
        }
        finally
        {
            MessageBoxService.ResetNativeForTests();
        }
    }

    [Fact]
    public void Show_UsesNativeShow_ForNonOkDialogs()
    {
        var native = new RecordingMessageBoxNative { HasDialog = true };
        MessageBoxService.SetNativeForTests(native);

        try
        {
            MessageBoxResult result = MessageBoxService.Show("test", "caption", MessageBoxButton.YesNo, MessageBoxImage.Question);

            Assert.Equal(MessageBoxResult.OK, result);
            Assert.Equal(1, native.ShowCallCount);
            Assert.Equal(0, native.FindWindowCallCount);
        }
        finally
        {
            MessageBoxService.ResetNativeForTests();
        }
    }

    [Fact]
    public async Task SetNativeForTests_IsolatesOverrides_PerExecutionContext()
    {
        var outerNative = new RecordingMessageBoxNative();
        var innerNative = new RecordingMessageBoxNative();
        MessageBoxService.SetNativeForTests(outerNative);

        try
        {
            _ = MessageBoxService.Show("outer-before", "caption", MessageBoxButton.YesNo, MessageBoxImage.Question);

            await Task.Run(() =>
            {
                MessageBoxService.SetNativeForTests(innerNative);
                try
                {
                    _ = MessageBoxService.Show("inner", "caption", MessageBoxButton.YesNo, MessageBoxImage.Question);
                }
                finally
                {
                    MessageBoxService.ResetNativeForTests();
                }
            });

            _ = MessageBoxService.Show("outer-after", "caption", MessageBoxButton.YesNo, MessageBoxImage.Question);

            Assert.Equal(2, outerNative.ShowCallCount);
            Assert.Single(innerNative.YesNoMessages);
            Assert.Equal("inner", innerNative.YesNoMessages[0].message);
            Assert.Equal(2, outerNative.YesNoMessages.Count);
            Assert.Equal("outer-before", outerNative.YesNoMessages[0].message);
            Assert.Equal("outer-after", outerNative.YesNoMessages[1].message);
        }
        finally
        {
            MessageBoxService.ResetNativeForTests();
        }
    }
}

