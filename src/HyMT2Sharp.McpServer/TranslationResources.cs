using System.ComponentModel;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Server;

namespace Sdcb.HyMT2Sharp.McpServer;

[McpServerResourceType]
public sealed class TranslationResources
{
    private static readonly string UiDir = Path.Combine(AppContext.BaseDirectory, "ui");

    [McpServerResource(UriTemplate = "ui://hymt2/translate", Name = "translate-workbench", MimeType = McpApps.HtmlMimeType)]
    [McpMeta("ui", """{"prefersBorder":true}""")]
    [Description("Interactive translation workbench UI (Hy-MT2, local and offline)")]
    public static string GetTranslateUi()
        => File.ReadAllText(Path.Combine(UiDir, "translate.html"));

    [McpServerResource(UriTemplate = "data://hymt2/languages", Name = "hymt2-languages", MimeType = "application/json")]
    [Description("Languages supported by Hy-MT2, grouped by tier: core (well-verified), extended, regional")]
    public static string GetLanguages() => LanguagesJson;

    private const string LanguagesJson = """
        {
          "core": [
            {"code": "zh", "name": "Chinese"},
            {"code": "zh_Hant", "name": "Traditional Chinese"},
            {"code": "en", "name": "English"},
            {"code": "ja", "name": "Japanese"},
            {"code": "ko", "name": "Korean"},
            {"code": "de", "name": "German"},
            {"code": "fr", "name": "French"},
            {"code": "es", "name": "Spanish"},
            {"code": "pt", "name": "Portuguese"},
            {"code": "it", "name": "Italian"},
            {"code": "ru", "name": "Russian"},
            {"code": "ar", "name": "Arabic"},
            {"code": "th", "name": "Thai"},
            {"code": "vi", "name": "Vietnamese"},
            {"code": "id", "name": "Indonesian"},
            {"code": "ms", "name": "Malay"},
            {"code": "tl", "name": "Filipino"},
            {"code": "hi", "name": "Hindi"}
          ],
          "extended": [
            {"code": "pl", "name": "Polish"},
            {"code": "cs", "name": "Czech"},
            {"code": "nl", "name": "Dutch"},
            {"code": "uk", "name": "Ukrainian"},
            {"code": "he", "name": "Hebrew"},
            {"code": "fa", "name": "Persian"},
            {"code": "tr", "name": "Turkish"},
            {"code": "km", "name": "Khmer"},
            {"code": "my", "name": "Burmese"},
            {"code": "gu", "name": "Gujarati"},
            {"code": "ur", "name": "Urdu"},
            {"code": "te", "name": "Telugu"},
            {"code": "mr", "name": "Marathi"},
            {"code": "bn", "name": "Bengali"},
            {"code": "ta", "name": "Tamil"}
          ],
          "regional": [
            {"code": "yue", "name": "Cantonese"},
            {"code": "bo", "name": "Tibetan"},
            {"code": "kk", "name": "Kazakh"},
            {"code": "mn", "name": "Mongolian"},
            {"code": "ug", "name": "Uyghur"}
          ]
        }
        """;
}
