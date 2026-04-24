using AudioPilot.Helpers;
using AudioPilot.ViewModels;

namespace AudioPilot.Tests.Helpers;

public sealed class DialogWindowHelperTests
{
    [Fact]
    public void ResolveConfirmationDecision_ReturnsShouldConfirm_WhenPackagedAppSelectionIsAvailable()
    {
        var viewModel = new PackagedAppPickerViewModel(
        [
            new AudioDeviceHelper.PackagedAppIdentity("Spotify", "Spotify.App", "Spotify.Package", "Spotify")
        ]);
        viewModel.TrySelectAppUserModelId("Spotify.App");

        DialogConfirmationDecision decision = DialogWindowHelper.ResolveConfirmationDecision<PackagedAppPickerViewModel>(
            viewModel,
            static current => current.CanConfirmSelection);

        Assert.True(decision.HasExpectedViewModel);
        Assert.True(decision.CanConfirm);
        Assert.True(decision.ShouldConfirm);
        Assert.Equal("Spotify.App", viewModel.SelectedAppUserModelId);
    }

    [Fact]
    public void ResolveConfirmationDecision_ReturnsCannotConfirm_WhenPackagedAppSelectionIsUnavailable()
    {
        var viewModel = new PackagedAppPickerViewModel([])
        {
            SelectedApp = null
        };

        DialogConfirmationDecision decision = DialogWindowHelper.ResolveConfirmationDecision<PackagedAppPickerViewModel>(
            viewModel,
            static current => current.CanConfirmSelection);

        Assert.True(decision.HasExpectedViewModel);
        Assert.False(decision.CanConfirm);
        Assert.False(decision.ShouldConfirm);
        Assert.Equal(string.Empty, viewModel.SelectedAppUserModelId);
    }

    [Fact]
    public void ResolveConfirmationDecision_ReturnsMissingViewModel_WhenDataContextTypeDoesNotMatch()
    {
        DialogConfirmationDecision decision = DialogWindowHelper.ResolveConfirmationDecision<PackagedAppPickerViewModel>(
            dataContext: new object(),
            static current => current.CanConfirmSelection);

        Assert.False(decision.HasExpectedViewModel);
        Assert.False(decision.CanConfirm);
        Assert.False(decision.ShouldConfirm);
    }
}
