using System.Collections.Generic;

namespace PlayGif.Common
{
    // Maps Playnite's locale codes to Steam's store API language names.
    //
    // Steam's naming is irregular and does not follow any standard: Korean is
    // "koreana" (not "korean"), Brazilian Portuguese is "brazilian", simplified
    // Chinese is "schinese", and Latin American Spanish is "latam". Sending an
    // unrecognised name makes the API silently return English, which is exactly
    // the bug this map exists to prevent — so every value here was verified
    // against the live API before being added.
    public static class SteamLanguage
    {
        private static readonly Dictionary<string, string> Map =
            new Dictionary<string, string>
        {
            { "ar", "arabic" },
            { "bg", "bulgarian" },
            { "cs", "czech" },
            { "da", "danish" },
            { "de", "german" },
            { "el", "greek" },
            { "es", "spanish" },
            { "fi", "finnish" },
            { "fr", "french" },
            { "he", "hebrew" },
            { "hu", "hungarian" },
            { "id", "indonesian" },
            { "it", "italian" },
            { "ja", "japanese" },
            { "ko", "koreana" },
            { "nl", "dutch" },
            { "no", "norwegian" },
            { "pl", "polish" },
            { "pt", "portuguese" },
            { "ro", "romanian" },
            { "ru", "russian" },
            { "sv", "swedish" },
            { "th", "thai" },
            { "tr", "turkish" },
            { "uk", "ukrainian" },
            { "vi", "vietnamese" },
            { "zh", "schinese" }
        };

        // Region-specific codes that differ from the bare language above
        private static readonly Dictionary<string, string> RegionMap =
            new Dictionary<string, string>
        {
            { "pt_br", "brazilian" },
            { "zh_tw", "tchinese" },
            { "zh_hk", "tchinese" },
            { "es_mx", "latam" },
            { "es_419", "latam" }
        };

        // Converts a Playnite language code ("ko_KR", "pt_BR", "en_US") to the
        // Steam API's name. Returns null for English or anything unrecognised,
        // in which case the caller should omit the parameter entirely.
        public static string FromPlayniteLanguage(string playniteLanguage)
        {
            if (string.IsNullOrWhiteSpace(playniteLanguage)) return null;

            var code = playniteLanguage.Trim().Replace('-', '_').ToLowerInvariant();

            // Playnite's default is the literal string "english" rather than a locale
            if (code == "english" || code.StartsWith("en")) return null;

            if (RegionMap.TryGetValue(code, out var regional)) return regional;

            var lang = code.Split('_')[0];
            return Map.TryGetValue(lang, out var mapped) ? mapped : null;
        }
    }
}
