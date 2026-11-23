using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace BIMinFormation.Ids
{
    /// <summary>
    /// Interface languages: Brazilian Portuguese (the text in the code) and English (<c>Text/en.txt</c>, embedded). Every
    /// visible phrase goes through <see cref="T"/> or <see cref="F"/>; without a translation the Portuguese text is kept.
    /// The logs are in English.
    /// </summary>
    public static class Tr
    {
        public const string Portuguese = "pt-BR";
        public const string English = "en";

        private static readonly Lazy<Dictionary<string, string>> EnglishTexts = new Lazy<Dictionary<string, string>>(Load);

        /// <summary>Language in use: <see cref="Portuguese"/> (default) or <see cref="English"/>.</summary>
        public static string Language { get; set; } = Portuguese;

        public static bool IsEnglish => Language == English;

        /// <summary>Phrase translated to the language in use.</summary>
        public static string T(string portuguese) =>
            IsEnglish && EnglishTexts.Value.TryGetValue(portuguese, out var english) ? english : portuguese;

        /// <summary>Marks a phrase for translation without translating it now (translated later with <see cref="T"/>).</summary>
        public static string N(string portuguese) => portuguese;

        /// <summary>Phrase with values (<c>{0}</c>, <c>{1}</c>…), translated and filled in.</summary>
        public static string F(string portuguese, params object?[] values) =>
            string.Format(CultureInfo.InvariantCulture, T(portuguese), values);

        /// <summary>English translations (for the completeness tests).</summary>
        public static IReadOnlyDictionary<string, string> EnglishDictionary => EnglishTexts.Value;

        /// <summary>
        /// <c>en.txt</c>: one translation per line, <c>Portuguese TAB English</c>; <c>\n</c> and <c>\t</c> written as such;
        /// empty lines and lines starting with <c>#</c> are ignored.
        /// </summary>
        public static Dictionary<string, string> Parse(TextReader reader)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0 || line[0] == '#') continue;
                int tab = line.IndexOf('\t');
                if (tab <= 0 || tab == line.Length - 1) continue; // no translation: keep the Portuguese text
                result[Unescape(line.Substring(0, tab))] = Unescape(line.Substring(tab + 1));
            }

            return result;
        }

        public static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\t", "\\t").Replace("\r", "");

        private static string Unescape(string text)
        {
            var result = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\\' && i + 1 < text.Length)
                {
                    char next = text[++i];
                    result.Append(next == 'n' ? '\n' : next == 't' ? '\t' : next);
                }
                else
                {
                    result.Append(text[i]);
                }
            }

            return result.ToString();
        }

        private static Dictionary<string, string> Load()
        {
            using var stream = typeof(Tr).GetTypeInfo().Assembly.GetManifestResourceStream("BIMinFormation.Ids.Text.en.txt");
            if (stream == null) return new Dictionary<string, string>(StringComparer.Ordinal);
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            return Parse(reader);
        }
    }
}
