using System.Collections.Generic;

namespace BIMinFormation.Ids
{
    /// <summary>
    /// Value of an IDS facet: a simple value (<c>simpleValue</c>) or an XSD restriction (<c>xs:restriction</c>).
    /// </summary>
    public abstract class IdsValue
    {
        /// <summary>Line of the element in the file (0 when unknown).</summary>
        public int Line { get; }

        protected IdsValue(int line)
        {
            Line = line;
        }
    }

    /// <summary><c>&lt;simpleValue&gt;</c>: exact comparison (case-sensitive).</summary>
    public sealed class SimpleValue : IdsValue
    {
        public string Text { get; }

        public SimpleValue(string text, int line = 0) : base(line)
        {
            Text = text;
        }

        public override string ToString() => Text;
    }

    /// <summary>
    /// <c>&lt;xs:restriction&gt;</c>. Each list keeps the facets in file order. Numeric and length bounds stay as text:
    /// their meaning depends on the <c>dataType</c> and is resolved by the rules engine.
    /// </summary>
    public sealed class RestrictionValue : IdsValue
    {
        public string? Base { get; }
        public IReadOnlyList<string> Enumerations { get; }
        public IReadOnlyList<string> Patterns { get; }
        public string? MinInclusive { get; }
        public string? MaxInclusive { get; }
        public string? MinExclusive { get; }
        public string? MaxExclusive { get; }
        public string? Length { get; }
        public string? MinLength { get; }
        public string? MaxLength { get; }
        public string? TotalDigits { get; }
        public string? FractionDigits { get; }

        public RestrictionValue(
            string? @base,
            IReadOnlyList<string> enumerations,
            IReadOnlyList<string> patterns,
            string? minInclusive,
            string? maxInclusive,
            string? minExclusive,
            string? maxExclusive,
            string? length,
            string? minLength,
            string? maxLength,
            string? totalDigits,
            string? fractionDigits,
            int line = 0) : base(line)
        {
            Base = @base;
            Enumerations = enumerations;
            Patterns = patterns;
            MinInclusive = minInclusive;
            MaxInclusive = maxInclusive;
            MinExclusive = minExclusive;
            MaxExclusive = maxExclusive;
            Length = length;
            MinLength = minLength;
            MaxLength = maxLength;
            TotalDigits = totalDigits;
            FractionDigits = fractionDigits;
        }

        public bool HasBounds =>
            MinInclusive != null || MaxInclusive != null || MinExclusive != null || MaxExclusive != null;

        public bool HasLengths => Length != null || MinLength != null || MaxLength != null;

        /// <summary>
        /// Possible values when the restriction is a closed list: an enumeration, or a single pattern of the form
        /// <c>A|B|C</c> with literal text only (common in BIMPR). Returns <c>null</c> when the restriction is not a closed list.
        /// </summary>
        public IReadOnlyList<string>? ClosedList()
        {
            if (Enumerations.Count > 0 && Patterns.Count == 0)
            {
                return Enumerations;
            }

            if (Patterns.Count == 1 && Enumerations.Count == 0)
            {
                return XsdRegex.TryLiteralAlternatives(Patterns[0]);
            }

            return null;
        }
    }
}
