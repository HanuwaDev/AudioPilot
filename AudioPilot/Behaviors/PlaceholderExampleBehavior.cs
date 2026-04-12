using System.Windows;
using System.Windows.Controls;
using Microsoft.Xaml.Behaviors;

namespace AudioPilot.Behaviors
{
    public static class TextBoxPlaceholderBehavior
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
            "Text",
            typeof(string),
            typeof(TextBoxPlaceholderBehavior),
            new FrameworkPropertyMetadata(string.Empty));

        private static readonly DependencyProperty StableTextProperty = DependencyProperty.RegisterAttached(
            "StableText",
            typeof(string),
            typeof(TextBoxPlaceholderBehavior),
            new FrameworkPropertyMetadata(string.Empty));

        public static string GetText(DependencyObject obj)
        {
            return (string)obj.GetValue(TextProperty);
        }

        public static void SetText(DependencyObject obj, string value)
        {
            obj.SetValue(TextProperty, value);
        }

        internal static string EnsureStableText(
            DependencyObject obj,
            string? options,
            string? identity = null,
            Func<string?, string>? pickDefaultOption = null,
            string? preferredFamily = null)
        {
            ArgumentNullException.ThrowIfNull(obj);

            string stableText = GetStableText(obj);
            if (string.IsNullOrWhiteSpace(stableText))
            {
                stableText = PlaceholderExampleBehavior.PickDeterministicOption(options, identity, pickDefaultOption, preferredFamily);
                SetStableText(obj, stableText);
            }

            SetText(obj, stableText);
            return stableText;
        }

        private static string GetStableText(DependencyObject obj)
        {
            return (string)obj.GetValue(StableTextProperty);
        }

        private static void SetStableText(DependencyObject obj, string value)
        {
            obj.SetValue(StableTextProperty, value);
        }
    }

    public sealed class PlaceholderExampleBehavior : Behavior<TextBox>
    {
        public string? Options { get; set; }

        public string? Identity { get; set; }

        public string? PreferredFamily { get; set; }

        protected override void OnAttached()
        {
            base.OnAttached();

            if (AssociatedObject == null)
            {
                return;
            }

            TextBoxPlaceholderBehavior.EnsureStableText(
                AssociatedObject,
                Options,
                Identity,
                preferredFamily: PreferredFamily);
        }

        internal static string PickDeterministicOption(
            string? options,
            string? identity = null,
            Func<string?, string>? pickDefaultOption = null,
            string? preferredFamily = null)
        {
            string[] candidates = GetCandidates(options);
            if (candidates.Length == 0)
            {
                return string.Empty;
            }

            string[] preferredCandidates = GetPreferredCandidates(candidates, preferredFamily);
            if (preferredCandidates.Length != 0)
            {
                candidates = preferredCandidates;
            }

            if (string.IsNullOrWhiteSpace(identity))
            {
                return (pickDefaultOption ?? PickDefaultOptionFromOptions)(options);
            }

            int index = ComputeStableIndex(identity, candidates.Length);
            return candidates[index];
        }

        internal static string PickDefaultOptionFromOptions(string? options)
        {
            string[] candidates = GetCandidates(options);

            if (candidates.Length == 0)
            {
                return string.Empty;
            }

            return candidates[Random.Shared.Next(candidates.Length)];
        }

        private static string[] GetCandidates(string? options)
        {
            if (string.IsNullOrWhiteSpace(options))
            {
                return [];
            }

            return [.. options
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static candidate => !string.IsNullOrWhiteSpace(candidate))];
        }

        private static string[] GetPreferredCandidates(string[] candidates, string? preferredFamily)
        {
            if (string.IsNullOrWhiteSpace(preferredFamily))
            {
                return [];
            }

            return [.. candidates.Where(candidate => MatchesPreferredFamily(candidate, preferredFamily))];
        }

        private static bool MatchesPreferredFamily(string candidate, string preferredFamily)
        {
            string normalizedFamily = preferredFamily.Trim().ToLowerInvariant();
            return normalizedFamily switch
            {
                "letter" => IsLetterBased(candidate),
                "upward" => IsUpward(candidate),
                "downward" => IsDownward(candidate),
                "directional-next" => IsDirectionalNext(candidate),
                "directional-previous" => IsDirectionalPrevious(candidate),
                "mouse-left" => IsMouseLeft(candidate),
                "mouse-right" => IsMouseRight(candidate),
                "mouse-middle" => IsMouseMiddle(candidate),
                "mouse-x1" => IsMouseX1(candidate),
                "mouse-x2" => IsMouseX2(candidate),
                "wheel-up" => IsWheelUp(candidate),
                "wheel-down" => IsWheelDown(candidate),
                _ => false,
            };
        }

        private static bool IsLetterBased(string candidate)
        {
            string token = GetMainToken(candidate);
            return token.Length == 1 && char.IsLetter(token[0]);
        }

        private static bool IsUpward(string candidate)
        {
            string token = GetMainToken(candidate);
            return token is "Up" or "PageUp" or "Home" or "WheelUp";
        }

        private static bool IsDownward(string candidate)
        {
            string token = GetMainToken(candidate);
            return token is "Down" or "PageDown" or "End" or "WheelDown";
        }

        private static bool IsDirectionalNext(string candidate)
        {
            string token = GetMainToken(candidate);
            return token is "Right" or "N" or "MouseRight" or "MouseX2";
        }

        private static bool IsDirectionalPrevious(string candidate)
        {
            string token = GetMainToken(candidate);
            return token is "Left" or "MouseLeft" or "MouseX1";
        }

        private static bool IsMouseLeft(string candidate)
        {
            string token = GetMainToken(candidate);
            return token == "MouseLeft";
        }

        private static bool IsMouseRight(string candidate)
        {
            string token = GetMainToken(candidate);
            return token == "MouseRight";
        }

        private static bool IsMouseMiddle(string candidate)
        {
            string token = GetMainToken(candidate);
            return token == "MouseMiddle";
        }

        private static bool IsMouseX1(string candidate)
        {
            string token = GetMainToken(candidate);
            return token == "MouseX1";
        }

        private static bool IsMouseX2(string candidate)
        {
            string token = GetMainToken(candidate);
            return token == "MouseX2";
        }

        private static bool IsWheelUp(string candidate)
        {
            string token = GetMainToken(candidate);
            return token == "WheelUp";
        }

        private static bool IsWheelDown(string candidate)
        {
            string token = GetMainToken(candidate);
            return token == "WheelDown";
        }

        private static string GetMainToken(string candidate)
        {
            string[] parts = candidate.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parts.Length == 0 ? string.Empty : parts[^1];
        }

        private static int ComputeStableIndex(string identity, int length)
        {
            unchecked
            {
                const uint offsetBasis = 2166136261;
                const uint prime = 16777619;

                uint hash = offsetBasis;
                foreach (char character in identity)
                {
                    hash ^= character;
                    hash *= prime;
                }

                return (int)(hash % length);
            }
        }
    }
}
