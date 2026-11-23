using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace BIMinFormation.Ids
{
    /// <summary>
    /// Converts <c>xs:pattern</c> patterns (XSD syntax) to .NET <see cref="Regex"/>. Differences handled: an XSD pattern
    /// is always anchored; <c>^</c> and <c>$</c> are literals; <c>\i</c>, <c>\I</c>, <c>\c</c>, <c>\C</c> do not exist in
    /// .NET. Character class subtraction (<c>[a-z-[aeiou]]</c>) is supported natively by .NET.
    /// </summary>
    public static class XsdRegex
    {
        private const string NameStartChar = @"_:A-Za-zÀ-ÖØ-öø-˿Ͱ-ͽͿ-῿‌-‍⁰-↏Ⰰ-⿯、-퟿豈-﷏ﷰ-�";
        private const string NameChar = NameStartChar + @"\-.0-9·̀-ͯ‿-⁀";

        public static bool TryCreate(string xsdPattern, out Regex? regex, out string? error)
        {
            try
            {
                regex = new Regex("^(?:" + Translate(xsdPattern) + ")$",
                    RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                error = null;
                return true;
            }
            catch (ArgumentException ex)
            {
                regex = null;
                error = ex.Message;
                return false;
            }
        }

        public static string Translate(string xsdPattern)
        {
            var result = new StringBuilder(xsdPattern.Length + 16);
            int classDepth = 0;

            for (int i = 0; i < xsdPattern.Length; i++)
            {
                char c = xsdPattern[i];

                if (c == '\\' && i + 1 < xsdPattern.Length)
                {
                    char next = xsdPattern[i + 1];
                    i++;
                    switch (next)
                    {
                        case 'i':
                            result.Append(classDepth > 0 ? NameStartChar : "[" + NameStartChar + "]");
                            break;
                        case 'I':
                            result.Append("[^" + NameStartChar + "]");
                            break;
                        case 'c':
                            result.Append(classDepth > 0 ? NameChar : "[" + NameChar + "]");
                            break;
                        case 'C':
                            result.Append("[^" + NameChar + "]");
                            break;
                        default:
                            result.Append('\\').Append(next);
                            break;
                    }

                    continue;
                }

                if (c == '[')
                {
                    classDepth++;
                }
                else if (c == ']' && classDepth > 0)
                {
                    classDepth--;
                }
                else if ((c == '^' || c == '$') && classDepth == 0)
                {
                    // In XSD, ^ and $ outside a class are ordinary characters.
                    result.Append('\\').Append(c);
                    continue;
                }

                result.Append(c);
            }

            return result.ToString();
        }

        /// <summary>
        /// If the pattern is only a list of literal alternatives (<c>A|B|C</c>), returns the values; otherwise <c>null</c>.
        /// Accepts escaped metacharacters (<c>\.</c>).
        /// </summary>
        public static IReadOnlyList<string>? TryLiteralAlternatives(string xsdPattern)
        {
            var values = new List<string>();
            var current = new StringBuilder();

            for (int i = 0; i < xsdPattern.Length; i++)
            {
                char c = xsdPattern[i];
                if (c == '\\')
                {
                    if (i + 1 >= xsdPattern.Length || !IsMeta(xsdPattern[i + 1]))
                    {
                        return null;
                    }

                    current.Append(xsdPattern[++i]);
                }
                else if (c == '|')
                {
                    values.Add(current.ToString());
                    current.Clear();
                }
                else if (IsMeta(c))
                {
                    return null;
                }
                else
                {
                    current.Append(c);
                }
            }

            values.Add(current.ToString());
            return values;
        }

        private static bool IsMeta(char c) => "\\.?*+{}()[]|".IndexOf(c) >= 0;
    }
}
