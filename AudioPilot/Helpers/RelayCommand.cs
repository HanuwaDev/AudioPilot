using System.ComponentModel;
using System.Windows.Input;

namespace AudioPilot.Helpers
{
    public class RelayCommand : ICommand, IDisposable
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;
        private HashSet<INotifyPropertyChanged>? _observedSources;
        private readonly Lock _lock = new();

        public RelayCommand(Action execute)
            : this(_ => execute(), _ => true) { }

        public RelayCommand(Action execute, Func<bool> canExecute)
            : this(_ => execute(), _ => canExecute()) { }

        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
            : this(execute, canExecute, null) { }

        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute, INotifyPropertyChanged? observedSource)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;

            if (observedSource != null)
            {
                _observedSources = [observedSource];
                observedSource.PropertyChanged += OnObservedPropertyChanged;
            }
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

        public void Execute(object? parameter) => _execute(parameter);

        private EventHandler? _canExecuteChanged;

        public event EventHandler? CanExecuteChanged
        {
            add
            {
                lock (_lock)
                {
                    _canExecuteChanged += value;
                }
            }
            remove
            {
                lock (_lock)
                {
                    _canExecuteChanged -= value;
                }
            }
        }

        private void OnObservedPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            RaiseCanExecuteChanged();
        }

        public void RaiseCanExecuteChanged()
        {
            EventHandler? handler;
            lock (_lock)
            {
                handler = _canExecuteChanged;
            }
            handler?.Invoke(this, EventArgs.Empty);
        }

        public void AddObservedSource(INotifyPropertyChanged source)
        {
            _observedSources ??= [];

            if (_observedSources.Add(source))
            {
                source.PropertyChanged += OnObservedPropertyChanged;
                RaiseCanExecuteChanged();
            }
        }

        public void RemoveObservedSource(INotifyPropertyChanged source)
        {
            if (_observedSources == null)
            {
                return;
            }

            if (_observedSources.Remove(source))
            {
                source.PropertyChanged -= OnObservedPropertyChanged;
            }
        }

        public void Dispose()
        {
            if (_observedSources == null)
            {
                GC.SuppressFinalize(this);
                return;
            }

            foreach (var source in _observedSources)
            {
                source.PropertyChanged -= OnObservedPropertyChanged;
            }

            _observedSources.Clear();
            _observedSources = null;
            GC.SuppressFinalize(this);
        }
    }
}
