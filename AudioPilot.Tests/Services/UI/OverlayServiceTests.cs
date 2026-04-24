using AudioPilot.Models;
using AudioPilot.Tests.TestDoubles;

namespace AudioPilot.Tests.Services.UI;

public sealed class OverlayServiceTests
{
    [Fact]
    public void Show_ReusesExistingPresenter_AcrossCalls()
    {
        int createdCount = 0;
        var presenter = new RecordingOverlayPresenter();

        var service = new OverlayService(
            dispatch: action => action(),
            presenterFactory: _ =>
            {
                createdCount++;
                return presenter;
            });

        service.Show("First");
        service.Show("Second");
        service.Show(OverlayActionStateKind.Disabled, "Sound muted");
        service.Show(OverlayDeviceKind.Input, "Header", "Device");
        service.ShowMediaTrack("Skipped to next track", "Song", "Artist");

        Assert.True(service.HasPresenterForTests);
        Assert.Equal(1, createdCount);
        Assert.Equal(2, presenter.MessageUpdateCount);
        Assert.Equal(1, presenter.ActionUpdateCount);
        Assert.Equal(1, presenter.DeviceUpdateCount);
        Assert.Equal(1, presenter.MediaUpdateCount);
        Assert.Equal(5, presenter.ShowCount);
        Assert.Equal(5, presenter.ApplyDisplayOptionsCount);
    }

    [Fact]
    public void Show_WhenDispatchUnavailable_DoesNotThrow()
    {
        var service = new OverlayService(
            dispatch: _ => throw new InvalidOperationException("Dispatcher unavailable"),
            presenterFactory: _ => new RecordingOverlayPresenter());

        service.Show("No dispatcher");

        Assert.False(service.HasPresenterForTests);
    }

    [Fact]
    public void UpdateDisplayOptions_AppliesToExistingPresenter()
    {
        var presenter = new RecordingOverlayPresenter();
        var service = new OverlayService(
            dispatch: action => action(),
            presenterFactory: _ => presenter);

        service.Show("init");
        service.UpdateDisplayOptions(OverlayPosition.Center, 4.2);

        Assert.True(service.HasPresenterForTests);
        Assert.Equal(2, presenter.ApplyDisplayOptionsCount);
    }

    [Fact]
    public void Show_WhenOverlayDisabled_DoesNotPresentUntilReenabled()
    {
        int createdCount = 0;
        var presenter = new RecordingOverlayPresenter();

        var service = new OverlayService(
            dispatch: action => action(),
            presenterFactory: _ =>
            {
                createdCount++;
                return presenter;
            });

        service.UpdateEnabled(false);
        service.Show("Suppressed");

        Assert.False(service.HasPresenterForTests);
        Assert.Equal(0, createdCount);
        Assert.Equal(0, presenter.ShowCount);

        service.UpdateEnabled(true);
        service.Show("Visible");

        Assert.True(service.HasPresenterForTests);
        Assert.Equal(1, createdCount);
        Assert.Equal(1, presenter.ShowCount);
    }

    [Fact]
    public void ShowRoutine_UsesRoutinePresenterPath()
    {
        var presenter = new RecordingOverlayPresenter();
        var service = new OverlayService(
            dispatch: action => action(),
            presenterFactory: _ => presenter);

        service.ShowRoutine("Routine 1 - Output/Input", "Speakers", "Microphone");

        Assert.Equal(1, presenter.RoutineUpdateCount);
        Assert.Equal(1, presenter.ShowCount);
        Assert.Equal(1, presenter.ApplyDisplayOptionsCount);
    }

    [Fact]
    public void ShowRoutinePartial_UsesPartialRoutinePresenterPath()
    {
        var presenter = new RecordingOverlayPresenter();
        var service = new OverlayService(
            dispatch: action => action(),
            presenterFactory: _ => presenter);

        service.ShowRoutinePartial("Desk - Partial", "Speakers", null, null, "Microphone");

        Assert.Equal(1, presenter.RoutinePartialUpdateCount);
        Assert.Equal(1, presenter.ShowCount);
        Assert.Equal(1, presenter.ApplyDisplayOptionsCount);
    }

    [Fact]
    public void Show_ActionState_UsesActionPresenterPath()
    {
        var presenter = new RecordingOverlayPresenter();
        var service = new OverlayService(
            dispatch: action => action(),
            presenterFactory: _ => presenter);

        service.Show(OverlayActionStateKind.Enabled, "Sound unmuted");

        var (stateKind, message) = Assert.Single(presenter.ActionMessages);
        Assert.Equal(OverlayActionStateKind.Enabled, stateKind);
        Assert.Equal("Sound unmuted", message);
        Assert.Equal(1, presenter.ShowCount);
        Assert.Equal(1, presenter.ApplyDisplayOptionsCount);
    }

    [Fact]
    public void Dispose_DisposesCachedPresenters_AndClearsPresenterCache()
    {
        var presenters = new List<RecordingOverlayPresenter>();
        var service = new OverlayService(
            dispatch: action => action(),
            presenterFactory: _ =>
            {
                var presenter = new RecordingOverlayPresenter();
                presenters.Add(presenter);
                return presenter;
            });

        service.ShowStacked(
            [
                new OverlayService.OverlayStackItem(OverlayDeviceKind.Output, "Output", "Speakers"),
                new OverlayService.OverlayStackItem(OverlayDeviceKind.Input, "Input", "Mic")
            ]);

        service.Dispose();

        Assert.False(service.HasPresenterForTests);
        Assert.Equal(2, presenters.Count);
        Assert.All(presenters, presenter => Assert.Equal(1, presenter.DisposeCount));
    }

    [Fact]
    public void Show_WhenOverlayStackShrinks_DisposesUnusedPresenters()
    {
        var presenters = new List<RecordingOverlayPresenter>();
        var service = new OverlayService(
            dispatch: action => action(),
            presenterFactory: _ =>
            {
                var presenter = new RecordingOverlayPresenter();
                presenters.Add(presenter);
                return presenter;
            });

        service.ShowStacked(
            [
                new OverlayService.OverlayStackItem(OverlayDeviceKind.Output, "Output", "Speakers"),
                new OverlayService.OverlayStackItem(OverlayDeviceKind.Input, "Input", "Mic")
            ]);

        service.Show(OverlayDeviceKind.Error, "Failure", "Speakers");

        Assert.Equal(2, presenters.Count);
        Assert.Equal(0, presenters[0].DisposeCount);
        Assert.Equal(1, presenters[1].DisposeCount);
        Assert.Equal(2, presenters[0].ShowCount);
        Assert.Equal(1, presenters[1].ShowCount);
    }
}

