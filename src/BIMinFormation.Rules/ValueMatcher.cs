using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using BIMinFormation.Ids;

namespace BIMinFormation.Rules
{
    /// <summary>
    /// Compares an IDS value (<c>simpleValue</c> or <c>xs:restriction</c>) with a model value, by the IDS 1.0 rules:
    /// <list type="bullet">
    /// <item>text: exact equality, case-sensitive;</item>
    /// <item>number: the IDS text is converted; reals use the tolerance <c>x·(1±1e-6)±1e-6</c>, integers compare
    /// exactly;</item>
    /// <item>boolean: only <c>true</c>/<c>false</c> (and the XSD <c>1</c>/<c>0</c>), in lower case;</item>
    /// <item>restriction: enumeration with the same rules; several <c>pattern</c> facets combine with OR (XSD) and only
    /// apply to text; bounds are strict (no tolerance) and only apply to numbers; lengths only apply to text.</item>
    /// </list>
    /// </summary>
    public static class ValueMatcher
    {
        public const double Tolerance = 1e-6;

        private static readonly Regex NumberLexical = new Regex(
            @"^[+-]?([0-9]+(\.[0-9]*)?|\.[0-9]+)([eE][+-]?[0-9]+)?$", RegexOptions.CultureInvariant);

        private static readonly ConcurrentDictionary<string, Regex?> Patterns =
            new ConcurrentDictionary<string, Regex?>(StringComparer.Ordinal);

        public static bool Matches(IdsValue expected, StateValue actual)
        {
            if (actual.IsEmpty)
            {
                return false;
            }

            switch (expected)
            {
                case SimpleValue simple:
                    return MatchesSimple(simple.Text, actual);
                case RestrictionValue restriction:
                    return MatchesRestriction(restriction, actual);
                default:
                    return false;
            }
        }

        /// <summary>Compares plain text (entity, Pset, property, system and material names).</summary>
        public static bool Matches(IdsValue expected, string? actual) =>
            actual != null && Matches(expected, StateValue.Of(actual));

        public static bool MatchesSimple(string expected, StateValue actual)
        {
            switch (actual.Kind)
            {
                case ValueKind.String:
                    return string.Equals(expected, actual.Text, StringComparison.Ordinal);
                case ValueKind.Real:
                    return TryParseNumber(expected, out double real) && RealEquals(actual.Number, real);
                case ValueKind.Integer:
                    return TryParseNumber(expected, out double number) && number == actual.Integer;
                case ValueKind.Boolean:
                    return TryParseBoolean(expected, out bool flag) && flag == actual.Boolean;
                default:
                    return false;
            }
        }

        private static bool MatchesRestriction(RestrictionValue restriction, StateValue actual)
        {
            if (restriction.Enumerations.Count > 0)
            {
                bool any = false;
                foreach (var option in restriction.Enumerations)
                {
                    if (MatchesSimple(option, actual))
                    {
                        any = true;
                        break;
                    }
                }

                if (!any) return false;
            }

            if (restriction.Patterns.Count > 0)
            {
                if (actual.Kind != ValueKind.String) return false;
                bool any = false;
                foreach (var pattern in restriction.Patterns)
                {
                    var regex = Patterns.GetOrAdd(pattern, p => XsdRegex.TryCreate(p, out var r, out _) ? r : null);
                    if (regex != null && regex.IsMatch(actual.Text!))
                    {
                        any = true;
                        break;
                    }
                }

                if (!any) return false;
            }

            if (restriction.HasBounds)
            {
                if (actual.Kind != ValueKind.Real && actual.Kind != ValueKind.Integer) return false;
                double value = actual.Number;
                if (!Bound(restriction.MinInclusive, b => value >= b)) return false;
                if (!Bound(restriction.MaxInclusive, b => value <= b)) return false;
                if (!Bound(restriction.MinExclusive, b => value > b)) return false;
                if (!Bound(restriction.MaxExclusive, b => value < b)) return false;
            }

            if (restriction.HasLengths)
            {
                if (actual.Kind != ValueKind.String) return false;
                int length = CodePoints(actual.Text!);
                if (!Size(restriction.Length, n => length == n)) return false;
                if (!Size(restriction.MinLength, n => length >= n)) return false;
                if (!Size(restriction.MaxLength, n => length <= n)) return false;
            }

            return true;
        }

        private static bool Bound(string? text, Func<double, bool> test) =>
            text == null || (TryParseNumber(text, out double bound) && test(bound));

        private static bool Size(string? text, Func<int, bool> test) =>
            text == null || (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && test(n));

        /// <summary>
        /// IDS 1.0 real equality: <paramref name="actual"/> is in <c>[x·(1−1e-6)−1e-6, x·(1+1e-6)+1e-6]</c> (with the factors
        /// swapped for a negative x).
        /// </summary>
        public static bool RealEquals(double actual, double expected)
        {
            double a = expected * (1 - Tolerance);
            double b = expected * (1 + Tolerance);
            double low = Math.Min(a, b) - Tolerance;
            double high = Math.Max(a, b) + Tolerance;
            // a few ULPs of slack: double rounding must not fail a value exactly on the bound
            double slack = Math.Max(Math.Abs(low), Math.Abs(high)) * 4e-16;
            return actual >= low - slack && actual <= high + slack;
        }

        /// <summary>Number in XSD lexical form (decimal point, optional exponent; no comma).</summary>
        public static bool TryParseNumber(string text, out double value)
        {
            if (NumberLexical.IsMatch(text)
                && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }

            value = 0;
            return false;
        }

        public static bool TryParseBoolean(string text, out bool value)
        {
            switch (text)
            {
                case "true":
                case "1":
                    value = true;
                    return true;
                case "false":
                case "0":
                    value = false;
                    return true;
                default:
                    value = false;
                    return false;
            }
        }

        private static int CodePoints(string text)
        {
            int count = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    i++;
                }

                count++;
            }

            return count;
        }
    }
}
