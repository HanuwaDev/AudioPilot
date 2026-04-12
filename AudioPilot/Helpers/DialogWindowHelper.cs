using System.Diagnostics.CodeAnalysis;
using System.Windows;

namespace AudioPilot.Helpers
{
    internal readonly record struct DialogConfirmationDecision(bool HasExpectedViewModel, bool CanConfirm)
    {
        public bool ShouldConfirm => HasExpectedViewModel && CanConfirm;
    }

    internal static class DialogWindowHelper
    {
        public static void Initialize(Window window, object viewModel)
        {
            ArgumentNullException.ThrowIfNull(window);
            ArgumentNullException.ThrowIfNull(viewModel);

            window.DataContext = viewModel;
        }

        public static void ApplyOwnerOrMainWindowTheme(Window window)
        {
            ArgumentNullException.ThrowIfNull(window);
            WindowThemeResolver.ApplyOwnerOrMainWindowTheme(window);
        }

        public static bool? ShowOwnedDialog(Window dialog, Window? owner = null)
        {
            ArgumentNullException.ThrowIfNull(dialog);

            if (owner != null)
            {
                dialog.Owner = owner;
            }

            return dialog.ShowDialog();
        }

        public static bool? ShowAppOwnedDialog(Window dialog)
        {
            ArgumentNullException.ThrowIfNull(dialog);

            return ShowOwnedDialog(dialog, Application.Current?.MainWindow);
        }

        public static bool TryGetViewModel<TViewModel>(
            Window window,
            [NotNullWhen(true)] out TViewModel? viewModel,
            bool setDialogResultOnFailure = false)
            where TViewModel : class
        {
            ArgumentNullException.ThrowIfNull(window);

            viewModel = window.DataContext as TViewModel;
            if (viewModel != null)
            {
                return true;
            }

            if (setDialogResultOnFailure)
            {
                window.DialogResult = false;
            }

            return false;
        }

        public static bool TryConfirm<TViewModel>(
            Window window,
            Func<TViewModel, bool> canConfirm,
            bool setDialogResultOnFailure)
            where TViewModel : class
        {
            ArgumentNullException.ThrowIfNull(window);
            ArgumentNullException.ThrowIfNull(canConfirm);

            DialogConfirmationDecision decision = ResolveConfirmationDecision(window.DataContext, canConfirm);
            if (!decision.HasExpectedViewModel)
            {
                if (setDialogResultOnFailure)
                {
                    window.DialogResult = false;
                }

                return false;
            }

            if (!decision.CanConfirm)
            {
                if (setDialogResultOnFailure)
                {
                    window.DialogResult = false;
                }

                return false;
            }

            window.DialogResult = true;
            return true;
        }

        internal static DialogConfirmationDecision ResolveConfirmationDecision<TViewModel>(
            object? dataContext,
            Func<TViewModel, bool> canConfirm)
            where TViewModel : class
        {
            ArgumentNullException.ThrowIfNull(canConfirm);

            if (dataContext is not TViewModel viewModel)
            {
                return new DialogConfirmationDecision(HasExpectedViewModel: false, CanConfirm: false);
            }

            return new DialogConfirmationDecision(HasExpectedViewModel: true, CanConfirm: canConfirm(viewModel));
        }
    }
}
