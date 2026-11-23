using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using BIMinFormation.Ids;

namespace BIMinFormation.Core.Fill
{
    /// <summary>Nature of a Revit parameter, to check whether it suits the IDS property.</summary>
    public enum ParameterKind
    {
        Text,
        Integer,
        YesNo,
        Number,
        Other,
    }

    /// <summary>
    /// Link suggestions: which Revit parameters suit an IDS property (compatible type) and how similar the names are. A
    /// suggestion never applies on its own: the user confirms it.
    /// </summary>
    public static class LinkSuggestions
    {
        /// <summary>
        /// Does the parameter suit the property? Text accepts any parameter (the exporter writes the formatted value, as Revit
        /// shows it); numbers require the same quantity (<paramref name="expectedSpec"/>, e.g. <c>Area</c>).
        /// </summary>
        public static bool Compatible(string? idsDataType, string? expectedSpec, ParameterKind kind, string? spec)
        {
            switch (IfcDataTypes.Classify(idsDataType))
            {
                case IfcValueKind.Boolean:
                    return kind == ParameterKind.YesNo;
                case IfcValueKind.Integer:
                    return kind == ParameterKind.Integer;
                case IfcValueKind.Real:
                    return kind == ParameterKind.Number && (expectedSpec == null || string.Equals(expectedSpec, spec, StringComparison.Ordinal));
                default:
                    return kind != ParameterKind.Other;
            }
        }

        /// <summary>
        /// Similarity between the property name and the parameter name (0 to 1+): share of the property words found in the
        /// parameter, ignoring accents and case, with a bonus when the parameter has no extra words.
        /// </summary>
        public static double Score(string property, string parameter)
        {
            var wanted = Words(property);
            var offered = Words(parameter);
            if (wanted.Count == 0 || offered.Count == 0) return 0;
            int matched = wanted.Count(w => offered.Any(o => Same(w, o)));
            double score = (double)matched / wanted.Count;
            if (matched > 0 && offered.All(o => wanted.Any(w => Same(w, o)))) score += 0.25;
            return score;
        }

        private static bool Same(string a, string b) =>
            a == b || (a.Length >= 4 && b.Length >= 4 && (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal)));

        /// <summary>Words without accents, in lower case; drops connecting words.</summary>
        public static List<string> Words(string text)
        {
            var plain = new StringBuilder();
            foreach (char c in text.Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
                plain.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
            }

            return plain.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => !Connectors.Contains(w))
                .ToList();
        }

        private static readonly HashSet<string> Connectors = new HashSet<string> { "de", "da", "do", "das", "dos", "e", "a", "o", "of", "the" };
    }
}
