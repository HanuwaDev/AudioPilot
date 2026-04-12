using System.IO;

namespace AudioPilot.Helpers
{
    /// <summary>
    /// Normalizes and classifies routine trigger targets so executable paths and packaged app identifiers can be
    /// compared and displayed consistently across routine save, execution, and monitoring flows.
    /// </summary>
    internal static class RoutineTriggerPathHelper
    {
        /// <summary>
        /// Normalizes a routine trigger target into the canonical form used for persistence and comparisons.
        /// </summary>
        internal static string NormalizeTriggerTarget(string? rawValue)
        {
            if (LooksLikePackagedAppId(rawValue))
            {
                return NormalizePackagedAppId(rawValue);
            }

            return NormalizeExecutablePath(rawValue);
        }

        /// <summary>
        /// Produces a normalized executable path by trimming quotes, expanding environment variables, and resolving
        /// rooted paths to their full filesystem representation when possible.
        /// </summary>
        internal static string NormalizeExecutablePath(string? rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                return string.Empty;
            }

            string trimmed = rawPath.Trim().Trim('"');
            if (trimmed.Length == 0)
            {
                return string.Empty;
            }

            try
            {
                if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? candidateUri) && candidateUri.IsFile)
                {
                    trimmed = candidateUri.LocalPath;
                }

                string expanded = Environment.ExpandEnvironmentVariables(trimmed)
                    .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                if (!Path.IsPathRooted(expanded))
                {
                    return expanded;
                }

                return Path.GetFullPath(expanded);
            }
            catch
            {
                return trimmed;
            }
        }

        internal static bool IsExecutablePathMatch(string? leftPath, string? rightPath)
        {
            string normalizedLeft = NormalizeExecutablePath(leftPath);
            string normalizedRight = NormalizeExecutablePath(rightPath);

            return !string.IsNullOrWhiteSpace(normalizedLeft)
                && !string.IsNullOrWhiteSpace(normalizedRight)
                && string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool LooksLikeExecutablePath(string? rawPath)
        {
            string normalized = NormalizeExecutablePath(rawPath);
            return !string.IsNullOrWhiteSpace(normalized)
                && Path.IsPathRooted(normalized)
                && normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool LooksLikePackagedAppId(string? rawValue)
        {
            string normalized = NormalizePackagedAppId(rawValue);
            return !string.IsNullOrWhiteSpace(normalized)
                && normalized.Contains('!', StringComparison.Ordinal)
                && !Path.IsPathRooted(normalized);
        }

        internal static bool LooksLikeSupportedStartupTarget(string? rawValue)
        {
            return LooksLikeExecutablePath(rawValue) || LooksLikePackagedAppId(rawValue);
        }

        /// <summary>
        /// Returns a human-friendly display name for a trigger target for use in routine UI, diagnostics, and
        /// operation identifiers.
        /// </summary>
        internal static string GetTriggerDisplayName(string? rawValue)
        {
            if (LooksLikeExecutablePath(rawValue))
            {
                string normalizedPath = NormalizeExecutablePath(rawValue);
                string fileName = Path.GetFileNameWithoutExtension(normalizedPath);
                return string.IsNullOrWhiteSpace(fileName) ? "Application" : fileName;
            }

            if (LooksLikePackagedAppId(rawValue))
            {
                string normalized = NormalizePackagedAppId(rawValue);
                int exclamationIndex = normalized.IndexOf('!');
                string packageFamilyName = exclamationIndex > 0 ? normalized[..exclamationIndex] : normalized;
                int underscoreIndex = packageFamilyName.IndexOf('_');
                string cleaned = underscoreIndex > 0 ? packageFamilyName[..underscoreIndex] : packageFamilyName;
                cleaned = cleaned.Replace('.', ' ').Trim();
                return string.IsNullOrWhiteSpace(cleaned) ? "Packaged app" : cleaned;
            }

            return "Application";
        }

        internal static bool IsPackagedAppMatch(string? triggerTarget, string? appUserModelId)
        {
            string normalizedTrigger = NormalizePackagedAppId(triggerTarget);
            string normalizedAumid = NormalizePackagedAppId(appUserModelId);
            return !string.IsNullOrWhiteSpace(normalizedTrigger)
                && !string.IsNullOrWhiteSpace(normalizedAumid)
                && string.Equals(normalizedTrigger, normalizedAumid, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizePackagedAppId(string? rawValue)
        {
            return string.IsNullOrWhiteSpace(rawValue)
                ? string.Empty
                : rawValue.Trim().Trim('"');
        }
    }
}
