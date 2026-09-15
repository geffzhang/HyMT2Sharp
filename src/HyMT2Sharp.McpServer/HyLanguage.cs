using System.Text.Json.Serialization;

namespace Sdcb.HyMT2Sharp.McpServer;

/// <summary>
/// Languages supported by the Hy-MT2 family. The 1.8B model is verified
/// mainly on the core set; the extended set (regional scripts) is exposed
/// for experimentation and may degrade in quality.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<HyLanguage>))]
public enum HyLanguage
{
    // Core set (well-verified in practice)
    zh,        // Chinese
    zh_Hant,   // Traditional Chinese
    en,        // English
    ja,        // Japanese
    ko,        // Korean
    de,        // German
    fr,        // French
    es,        // Spanish
    pt,        // Portuguese
    it,        // Italian
    ru,        // Russian
    ar,        // Arabic
    th,        // Thai
    vi,        // Vietnamese
    id,        // Indonesian
    ms,        // Malay
    tl,        // Filipino
    hi,        // Hindi

    // Extended set (exposed, quality not individually verified on 1.8B)
    pl,        // Polish
    cs,        // Czech
    nl,        // Dutch
    uk,        // Ukrainian
    he,        // Hebrew
    fa,        // Persian
    tr,        // Turkish
    km,        // Khmer
    my,        // Burmese
    gu,        // Gujarati
    ur,        // Urdu
    te,        // Telugu
    mr,        // Marathi
    bn,        // Bengali
    ta,        // Tamil

    // Regional scripts officially supported by the Hy-MT2 family
    yue,       // Cantonese
    bo,        // Tibetan
    kk,        // Kazakh
    mn,        // Mongolian
    ug,        // Uyghur
}

public static class HyLanguageNames
{
    /// <summary>English display names used in the translation prompt.</summary>
    public static readonly Dictionary<HyLanguage, string> English = new()
    {
        [HyLanguage.zh] = "Chinese",
        [HyLanguage.zh_Hant] = "Traditional Chinese",
        [HyLanguage.en] = "English",
        [HyLanguage.ja] = "Japanese",
        [HyLanguage.ko] = "Korean",
        [HyLanguage.de] = "German",
        [HyLanguage.fr] = "French",
        [HyLanguage.es] = "Spanish",
        [HyLanguage.pt] = "Portuguese",
        [HyLanguage.it] = "Italian",
        [HyLanguage.ru] = "Russian",
        [HyLanguage.ar] = "Arabic",
        [HyLanguage.th] = "Thai",
        [HyLanguage.vi] = "Vietnamese",
        [HyLanguage.id] = "Indonesian",
        [HyLanguage.ms] = "Malay",
        [HyLanguage.tl] = "Filipino",
        [HyLanguage.hi] = "Hindi",
        [HyLanguage.pl] = "Polish",
        [HyLanguage.cs] = "Czech",
        [HyLanguage.nl] = "Dutch",
        [HyLanguage.uk] = "Ukrainian",
        [HyLanguage.he] = "Hebrew",
        [HyLanguage.fa] = "Persian",
        [HyLanguage.tr] = "Turkish",
        [HyLanguage.km] = "Khmer",
        [HyLanguage.my] = "Burmese",
        [HyLanguage.gu] = "Gujarati",
        [HyLanguage.ur] = "Urdu",
        [HyLanguage.te] = "Telugu",
        [HyLanguage.mr] = "Marathi",
        [HyLanguage.bn] = "Bengali",
        [HyLanguage.ta] = "Tamil",
        [HyLanguage.yue] = "Cantonese",
        [HyLanguage.bo] = "Tibetan",
        [HyLanguage.kk] = "Kazakh",
        [HyLanguage.mn] = "Mongolian",
        [HyLanguage.ug] = "Uyghur",
    };

    public static string ToEnglish(HyLanguage language)
        => English.TryGetValue(language, out string? name) ? name : language.ToString();
}

public static class TranslationPrompts
{
    /// <summary>
    /// The prompt shape validated in the HyMT2Sharp README: instruct the model
    /// to translate without extra explanation. The full-width colon is part of
    /// the official template; keep it verbatim.
    /// </summary>
    public static string Render(string text, HyLanguage target)
        => $"Translate the following segment into {HyLanguageNames.ToEnglish(target)}, without additional explanation：{text}";
}
