using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using BIMinFormation.Ids;
using BIMinFormation.Rules;

namespace BIMinFormation.Core.Classification
{
    public enum RecognitionStatus
    {
        /// <summary>No specification applies to the element, and it has no identity filled in.</summary>
        Unclassified,

        /// <summary>Exactly one specification applies to the element.</summary>
        Classified,

        /// <summary>More than one specification applies (common when recognized by entity): the requirements add up.</summary>
        Multiple,

        /// <summary>
        /// The element has an identity filled in (e.g. EOI) that matches no specification: typed by hand, copied from
        /// another project or from an old version of the .ids. A validator would not check it.
        /// </summary>
        OrphanIdentity,
    }

    public sealed class RecognitionResult
    {
        public ElementState Element { get; }
        public RecognitionStatus Status { get; }
        public IReadOnlyList<Specification> Specifications { get; }

        /// <summary>Identity filled in without a specification (e.g. <c>BIMPR.EOI = pr.11.05 parede</c>).</summary>
        public string? OrphanValue { get; }

        public RecognitionResult(ElementState element, RecognitionStatus status, IReadOnlyList<Specification> specifications,
            string? orphanValue)
        {
            Element = element;
            Status = status;
            Specifications = specifications;
            OrphanValue = orphanValue;
        }
    }

    /// <summary>Automatic recognition of each element's specifications, and of identities without a specification.</summary>
    public sealed class Recognizer
    {
        private readonly SpecificationIndex _index;

        /// <summary>Properties used as identity in the applicability (e.g. <c>BIMPR/EOI</c>).</summary>
        private readonly HashSet<(string Set, string Property)> _identityKeys;

        public Recognizer(IReadOnlyList<Specification> specifications)
        {
            _index = new SpecificationIndex(specifications);
            _identityKeys = new HashSet<(string, string)>(specifications
                .SelectMany(s => s.Applicability.Facets.OfType<PropertyFacet>())
                .Where(p => p.PropertySet is SimpleValue && p.BaseName is SimpleValue && p.Value is SimpleValue)
                .Select(p => (((SimpleValue)p.PropertySet).Text, ((SimpleValue)p.BaseName).Text)));
        }

        public RecognitionResult Recognize(ElementState element, ModelState model)
        {
            var applicable = _index.Applicable(element, model).Where(s => s.Usage != SpecificationUsage.Prohibited).ToList();
            if (applicable.Count == 1)
            {
                return new RecognitionResult(element, RecognitionStatus.Classified, applicable, null);
            }

            if (applicable.Count > 1)
            {
                return new RecognitionResult(element, RecognitionStatus.Multiple, applicable, null);
            }

            foreach (var set in element.PropertySets)
            {
                foreach (var property in set.Properties)
                {
                    if (_identityKeys.Contains((set.Name, property.Name)) && property.HasValue)
                    {
                        string value = string.Join(", ", property.Values.Where(v => !v.IsEmpty));
                        return new RecognitionResult(element, RecognitionStatus.OrphanIdentity, applicable,
                            set.Name + "." + property.Name + " = " + value);
                    }
                }
            }

            return new RecognitionResult(element, RecognitionStatus.Unclassified, applicable, null);
        }
    }

    /// <summary>
    /// Grid ordering: ignoring accents and case, in pt-BR, with numbers in natural order ("PR.2" before "PR.10").
    /// Ties keep the input order (LINQ ordering is stable).
    /// </summary>
    public sealed class PortugueseComparer : IComparer<string?>
    {
        public static PortugueseComparer Instance { get; } = new PortugueseComparer();

        private static readonly CompareInfo Compare = new CultureInfo("pt-BR").CompareInfo;

        int IComparer<string?>.Compare(string? x, string? y) => CompareText(x, y);

        public static int CompareText(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x == null) return -1;
            if (y == null) return 1;

            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                {
                    int si = i, sj = j;
                    while (i < x.Length && char.IsDigit(x[i])) i++;
                    while (j < y.Length && char.IsDigit(y[j])) j++;
                    string a = x.Substring(si, i - si).TrimStart('0');
                    string b = y.Substring(sj, j - sj).TrimStart('0');
                    if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                    int digits = string.CompareOrdinal(a, b);
                    if (digits != 0) return digits;
                    continue;
                }

                int si2 = i, sj2 = j;
                while (i < x.Length && !char.IsDigit(x[i])) i++;
                while (j < y.Length && !char.IsDigit(y[j])) j++;
                int text = Compare.Compare(x.Substring(si2, i - si2), y.Substring(sj2, j - sj2),
                    CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);
                if (text != 0) return text;
            }

            return (x.Length - i).CompareTo(y.Length - j);
        }

        /// <summary>Text without accents (for searching).</summary>
        public static string RemoveAccents(string text)
        {
            var builder = new StringBuilder(text.Length);
            foreach (char c in text.Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(c);
                }
            }

            return builder.ToString().Normalize(NormalizationForm.FormC);
        }
    }
}
