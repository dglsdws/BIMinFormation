using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace BIMinFormation.Ids
{
    /// <summary>Nature of the value of an IFC type, to validate the value written in the IDS.</summary>
    public enum IfcValueKind
    {
        Unknown,
        String,
        Integer,
        Real,
        Boolean,
    }

    /// <summary>
    /// Classification of the IFC defined types used in <c>dataType</c>. Covers the simple types; the full table per schema
    /// comes from the schema catalog.
    /// </summary>
    public static class IfcDataTypes
    {
        private static readonly HashSet<string> Strings = new HashSet<string>(StringComparer.Ordinal)
        {
            "IFCLABEL", "IFCTEXT", "IFCIDENTIFIER", "IFCDESCRIPTIVEMEASURE", "IFCURIREFERENCE", "IFCDATE",
            "IFCDATETIME", "IFCTIME", "IFCDURATION", "IFCGLOBALLYUNIQUEID", "IFCPRESENTABLETEXT", "IFCLANGUAGEID",
            "IFCBOXALIGNMENT", "IFCFONTSTYLE", "IFCFONTVARIANT", "IFCFONTWEIGHT", "IFCTEXTALIGNMENT",
            "IFCTEXTDECORATION", "IFCTEXTTRANSFORMATION", "IFCTEXTFONTNAME", "IFCBINARY", "IFCWELLKNOWNTEXTLITERAL",
        };

        private static readonly HashSet<string> Integers = new HashSet<string>(StringComparer.Ordinal)
        {
            "IFCINTEGER", "IFCPOSITIVEINTEGER", "IFCDIMENSIONCOUNT", "IFCTIMESTAMP", "IFCDAYINMONTHNUMBER",
            "IFCDAYINWEEKNUMBER", "IFCMONTHINYEARNUMBER", "IFCYEARNUMBER", "IFCHOURINDAY", "IFCMINUTEINHOUR",
            "IFCCARDINALPOINTREFERENCE", "IFCINTEGERCOUNTRATEMEASURE",
        };

        private static readonly HashSet<string> Booleans = new HashSet<string>(StringComparer.Ordinal)
        {
            "IFCBOOLEAN", "IFCLOGICAL",
        };

        private static readonly HashSet<string> NonScalar = new HashSet<string>(StringComparer.Ordinal)
        {
            "IFCCOMPLEXNUMBER", "IFCCOMPOUNDPLANEANGLEMEASURE",
        };

        private static readonly HashSet<string> OtherReals = new HashSet<string>(StringComparer.Ordinal)
        {
            "IFCREAL", "IFCPARAMETERVALUE", "IFCSECONDINMINUTE", "IFCPOSITIVEREAL",
        };

        private static readonly Regex IntegerLexical = new Regex(@"^[+-]?[0-9]+$", RegexOptions.CultureInvariant);

        private static readonly Regex RealLexical = new Regex(
            @"^([+-]?([0-9]+(\.[0-9]*)?|\.[0-9]+)([eE][+-]?[0-9]+)?|[+-]?INF|NaN)$", RegexOptions.CultureInvariant);

        /// <summary>
        /// Positive measure (<c>IFCPOSITIVELENGTHMEASURE</c>, <c>IFCPOSITIVEPLANEANGLEMEASURE</c>, <c>IFCPOSITIVERATIOMEASURE</c>):
        /// the IFC schema requires a value greater than zero, and the Revit exporter drops zero (the property does not come out).
        /// </summary>
        public static bool IsPositiveMeasure(string? dataType) =>
            dataType != null && dataType.StartsWith("IFCPOSITIVE", StringComparison.OrdinalIgnoreCase);

        public static IfcValueKind Classify(string? dataType)
        {
            if (dataType == null)
            {
                return IfcValueKind.Unknown;
            }

            if (Strings.Contains(dataType))
            {
                return IfcValueKind.String;
            }

            if (Integers.Contains(dataType))
            {
                return IfcValueKind.Integer;
            }

            if (Booleans.Contains(dataType))
            {
                return IfcValueKind.Boolean;
            }

            if (NonScalar.Contains(dataType))
            {
                return IfcValueKind.Unknown;
            }

            if (OtherReals.Contains(dataType) || dataType.EndsWith("MEASURE", StringComparison.Ordinal))
            {
                return IfcValueKind.Real;
            }

            return IfcValueKind.Unknown;
        }

        /// <summary>
        /// Checks the lexical form of an IDS value for the type (rules from the official cases: lower-case booleans, integers
        /// without decimals, numbers with a decimal point).
        /// </summary>
        public static bool IsLexicallyValid(string value, IfcValueKind kind)
        {
            switch (kind)
            {
                case IfcValueKind.Integer:
                    return IntegerLexical.IsMatch(value);
                case IfcValueKind.Real:
                    return RealLexical.IsMatch(value);
                case IfcValueKind.Boolean:
                    return value == "true" || value == "false";
                default:
                    return true;
            }
        }
    }
}
