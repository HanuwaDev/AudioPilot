using System.ComponentModel;
using AudioPilot.Helpers;

namespace AudioPilot.Tests.Helpers;

public class RelayCommandTests
{
    [Fact]
    public void AddObservedSource_RaisesCanExecuteChanged_OnSourcePropertyChanged()
    {
        var observed = new TestObservable();
        var command = new RelayCommand(() => { }, () => true);
        int raiseCount = 0;
        command.CanExecuteChanged += (_, _) => raiseCount++;

        command.AddObservedSource(observed);
        observed.RaisePropertyChanged(nameof(TestObservable.Value));

        Assert.True(raiseCount >= 2);
    }

    [Fact]
    public void RemoveObservedSource_StopsCanExecuteChangedNotifications()
    {
        var observed = new TestObservable();
        var command = new RelayCommand(() => { }, () => true);
        int raiseCount = 0;
        command.CanExecuteChanged += (_, _) => raiseCount++;

        command.AddObservedSource(observed);
        observed.RaisePropertyChanged(nameof(TestObservable.Value));
        int countAfterObserved = raiseCount;

        command.RemoveObservedSource(observed);
        observed.RaisePropertyChanged(nameof(TestObservable.Value));

        Assert.Equal(countAfterObserved, raiseCount);
    }

    [Fact]
    public void Dispose_UnsubscribesFromAllObservedSources()
    {
        var observedA = new TestObservable();
        var observedB = new TestObservable();
        var command = new RelayCommand(() => { }, () => true);
        int raiseCount = 0;
        command.CanExecuteChanged += (_, _) => raiseCount++;

        command.AddObservedSource(observedA);
        command.AddObservedSource(observedB);
        command.Dispose();

        int countAfterDispose = raiseCount;
        observedA.RaisePropertyChanged(nameof(TestObservable.Value));
        observedB.RaisePropertyChanged(nameof(TestObservable.Value));

        Assert.Equal(countAfterDispose, raiseCount);
    }

    private sealed class TestObservable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public int Value { get; set; }

        public void RaisePropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

