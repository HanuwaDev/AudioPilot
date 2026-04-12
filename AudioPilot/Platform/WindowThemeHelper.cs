using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using AudioPilot.Logging;
using AudioPilot.Models;
using Microsoft.Win32;

namespace AudioPilot.Platform
{
    public static partial class WindowThemeHelper
    {
        [LibraryImport("dwmapi.dll")]
        private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const string DarkThemeDictionaryUri = "pack://application:,,,/Themes/DarkTheme.xaml";
        private const string LightThemeDictionaryUri = "pack://application:,,,/Themes/LightTheme.xaml";
        private static readonly DependencyProperty LastAppliedEffectiveThemeProperty = DependencyProperty.RegisterAttached(
            "LastAppliedEffectiveTheme",
            typeof(AppTheme?),
            typeof(WindowThemeHelper),
            new PropertyMetadata(null));
        private static readonly DependencyProperty LastAppliedWindowHandleProperty = DependencyProperty.RegisterAttached(
            "LastAppliedWindowHandle",
            typeof(long),
            typeof(WindowThemeHelper),
            new PropertyMetadata(0L));

        public static bool IsSystemDarkTheme()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key?.GetValue("AppsUseLightTheme") is int useLightTheme)
                {
                    return useLightTheme == 0;
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("WindowThemeHelper", () => $"Failed to read system theme from registry: {ex.GetType().Name}");
            }
            return false;
        }

        public static AppTheme ResolveEffectiveTheme(AppTheme configuredTheme)
        {
            return configuredTheme switch
            {
                AppTheme.Dark => AppTheme.Dark,
                AppTheme.Light => AppTheme.Light,
                _ => IsSystemDarkTheme() ? AppTheme.Dark : AppTheme.Light
            };
        }

        public static void ApplyTheme(Window window, AppTheme theme)
        {
            try
            {
                if (window == null)
                {
                    Logger.Instance.Warning("WindowThemeHelper", "Window was null; skipping theme application.");
                    return;
                }

                var effectiveTheme = ResolveEffectiveTheme(theme);
                bool useDarkMode = effectiveTheme == AppTheme.Dark;
                string requestedThemeDictionaryUri = useDarkMode ? DarkThemeDictionaryUri : LightThemeDictionaryUri;

                var app = Application.Current;
                if (app != null)
                {
                    if (!HasExactThemeDictionaryState(app, requestedThemeDictionaryUri))
                    {
                        ResourceDictionary themeDict;
                        try
                        {
                            themeDict = new ResourceDictionary
                            {
                                Source = new Uri(requestedThemeDictionaryUri)
                            };
                        }
                        catch (Exception ex)
                        {
                            Logger.Instance.Warning("WindowThemeHelper", "Failed to load requested theme dictionary, falling back to LightTheme.", nameof(ApplyTheme), ex);

                            effectiveTheme = AppTheme.Light;
                            useDarkMode = false;
                            requestedThemeDictionaryUri = LightThemeDictionaryUri;
                            themeDict = new ResourceDictionary
                            {
                                Source = new Uri(LightThemeDictionaryUri)
                            };
                        }

                        for (int i = app.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
                        {
                            var dict = app.Resources.MergedDictionaries[i];
                            if (IsThemeDictionary(dict))
                            {
                                app.Resources.MergedDictionaries.RemoveAt(i);
                            }
                        }

                        app.Resources.MergedDictionaries.Insert(0, themeDict);
                    }
                }

                var windowHelper = new WindowInteropHelper(window);
                var handle = windowHelper.Handle;
                if (handle == IntPtr.Zero)
                {
                    Logger.Instance.Warning("WindowThemeHelper", "Window handle is zero, cannot apply title bar theme.");
                    return;
                }

                if (HasWindowThemeAlreadyApplied(window, handle, effectiveTheme))
                {
                    Logger.Instance.Trace("WindowThemeHelper", () => $"Skipped theme reapply for effectiveTheme={effectiveTheme}.");
                    return;
                }

                int useImmersiveDarkMode = useDarkMode ? 1 : 0;
                int result = 0;

                if (Environment.OSVersion.Version.Build >= 22000)
                {
                    result = DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useImmersiveDarkMode, sizeof(int));
                }
                else if (Environment.OSVersion.Version.Build >= 17763)
                {
                    result = DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref useImmersiveDarkMode, sizeof(int));
                }
                else
                {
                    Logger.Instance.Info("WindowThemeHelper", "OS version does not support immersive dark mode.");
                    RecordWindowTheme(window, handle, effectiveTheme);
                    return;
                }

                if (result != 0)
                {
                    Logger.Instance.Warning("WindowThemeHelper", () => $"DwmSetWindowAttribute failed with HRESULT: {result}");
                }
                else
                {
                    RecordWindowTheme(window, handle, effectiveTheme);
                    Logger.Instance.Info("WindowThemeHelper", () => $"Applied theme={effectiveTheme} (configured={theme}) including window chrome.");
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Error("WindowThemeHelper", () => $"Exception while applying theme: {ex.GetType().Name}");
            }
        }

        private static bool IsThemeDictionary(ResourceDictionary dictionary)
        {
            string? source = dictionary.Source?.OriginalString;
            return string.Equals(source, DarkThemeDictionaryUri, StringComparison.OrdinalIgnoreCase)
                || string.Equals(source, LightThemeDictionaryUri, StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasExactThemeDictionaryState(Application application, string expectedThemeDictionaryUri)
        {
            int themeDictionaryCount = 0;

            foreach (ResourceDictionary dictionary in application.Resources.MergedDictionaries)
            {
                if (!IsThemeDictionary(dictionary))
                {
                    continue;
                }

                themeDictionaryCount++;
                if (!string.Equals(dictionary.Source?.OriginalString, expectedThemeDictionaryUri, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return themeDictionaryCount == 1;
        }

        private static bool HasWindowThemeAlreadyApplied(Window window, IntPtr handle, AppTheme effectiveTheme)
        {
            AppTheme? lastAppliedTheme = (AppTheme?)window.GetValue(LastAppliedEffectiveThemeProperty);
            long lastAppliedHandle = (long)window.GetValue(LastAppliedWindowHandleProperty);
            return lastAppliedTheme == effectiveTheme && lastAppliedHandle == handle.ToInt64();
        }

        private static void RecordWindowTheme(Window window, IntPtr handle, AppTheme effectiveTheme)
        {
            window.SetValue(LastAppliedEffectiveThemeProperty, effectiveTheme);
            window.SetValue(LastAppliedWindowHandleProperty, handle.ToInt64());
        }
    }
}
