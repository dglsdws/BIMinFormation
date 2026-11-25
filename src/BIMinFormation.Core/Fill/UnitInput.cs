using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using BIMinFormation.Ids;

namespace BIMinFormation.Core.Fill
{
    /// <summary>
    /// A value typed with its unit ("200 mm", "30°", "20 °C", "1,5 t") converted to the SI unit the IDS uses for the measure
    /// (buildingSMART IDS, Documentation/UserManual/units.md: metre, square metre, cubic metre, kilogram, radian, kelvin,
    /// second…). Without a unit the text stays as it is: it is already in SI.
    /// </summary>
    public static class UnitInput
    {
        private sealed class Unit
        {
            public string[] Symbols { get; }
            public double Factor { get; }
            public double Offset { get; }

            public Unit(double factor, params string[] symbols) : this(factor, 0, symbols)
            {
            }

            public Unit(double factor, double offset, params string[] symbols)
            {
                Factor = factor;
                Offset = offset;
                Symbols = symbols;
            }
        }

        private static readonly Unit[] Length =
        {
            new Unit(0.001, "mm"), new Unit(0.01, "cm"), new Unit(0.1, "dm"), new Unit(1, "m"), new Unit(1000, "km"),
            new Unit(0.0254, "in", "\"", "pol"), new Unit(0.3048, "ft", "'", "pés", "pé"),
        };

        private static readonly Dictionary<string, Unit[]> ByMeasure = new Dictionary<string, Unit[]>(StringComparer.Ordinal)
        {
            ["LENGTH"] = Length,
            ["POSITIVELENGTH"] = Length,
            ["NONNEGATIVELENGTH"] = Length,
            ["AREA"] = new[]
            {
                new Unit(1e-6, "mm²", "mm2"), new Unit(1e-4, "cm²", "cm2"), new Unit(1, "m²", "m2"), new Unit(1e4, "ha"),
                new Unit(0.09290304, "ft²", "ft2"), new Unit(0.00064516, "in²", "in2"),
            },
            ["VOLUME"] = new[]
            {
                new Unit(1e-9, "mm³", "mm3"), new Unit(1e-6, "cm³", "cm3"), new Unit(1, "m³", "m3"), new Unit(0.001, "l", "L", "litro", "litros"),
                new Unit(0.028316846592, "ft³", "ft3"),
            },
            ["MASS"] = new[] { new Unit(0.001, "g"), new Unit(1, "kg"), new Unit(1000, "t"), new Unit(0.45359237, "lb") },
            ["PLANEANGLE"] = Angle(),
            ["POSITIVEPLANEANGLE"] = Angle(),
            ["THERMODYNAMICTEMPERATURE"] = new[]
            {
                new Unit(1, "K"), new Unit(1, 273.15, "°C", "ºC", "C"), new Unit(5.0 / 9, 273.15 - 32 * 5.0 / 9, "°F", "ºF", "F"),
            },
            ["POWER"] = new[] { new Unit(1, "W"), new Unit(1e3, "kW"), new Unit(1e6, "MW") },
            ["ELECTRICVOLTAGE"] = new[] { new Unit(1e-3, "mV"), new Unit(1, "V"), new Unit(1e3, "kV") },
            ["ELECTRICCURRENT"] = new[] { new Unit(1e-3, "mA"), new Unit(1, "A"), new Unit(1e3, "kA") },
            ["FREQUENCY"] = new[] { new Unit(1, "Hz"), new Unit(1e3, "kHz") },
            ["PRESSURE"] = new[] { new Unit(1, "Pa"), new Unit(1e3, "kPa"), new Unit(1e6, "MPa"), new Unit(1e5, "bar") },
            ["VOLUMETRICFLOWRATE"] = new[]
            {
                new Unit(1, "m³/s", "m3/s"), new Unit(1.0 / 3600, "m³/h", "m3/h"), new Unit(0.001, "l/s", "L/s"), new Unit(0.001 / 60, "l/min", "L/min"),
            },
            ["LINEARVELOCITY"] = new[] { new Unit(1, "m/s"), new Unit(1 / 3.6, "km/h") },
            ["FORCE"] = new[] { new Unit(1, "N"), new Unit(1e3, "kN") },
            ["ENERGY"] = new[] { new Unit(1, "J"), new Unit(1e3, "kJ"), new Unit(3.6e6, "kWh") },
            ["TIME"] = new[] { new Unit(1, "s"), new Unit(60, "min"), new Unit(3600, "h") },
        };

        private static Unit[] Angle() => new[] { new Unit(Math.PI / 180, "°", "º", "deg", "grau", "graus"), new Unit(1, "rad") };

        private static readonly Regex Number = new Regex(@"^\s*([-+]?(?:\d+(?:[.,]\d*)?|[.,]\d+)(?:[eE][-+]?\d+)?)\s*(\S.*?)?\s*$");

        /// <summary>Measure of an IDS dataType (<c>IFCLENGTHMEASURE</c> → <c>LENGTH</c>).</summary>
        private static string? Measure(string? dataType)
        {
            if (dataType == null) return null;
            string core = dataType.ToUpperInvariant();
            if (core.StartsWith("IFC", StringComparison.Ordinal)) core = core.Substring(3);
            if (core.EndsWith("MEASURE", StringComparison.Ordinal)) core = core.Substring(0, core.Length - "MEASURE".Length);
            return core;
        }

        /// <summary>The units accepted for the measure, for the hint ("mm, cm, m…"); <c>null</c> when it takes none.</summary>
        public static string? Accepted(string? dataType) =>
            Measure(dataType) is { } measure && ByMeasure.TryGetValue(measure, out var units)
                ? string.Join(", ", Array.ConvertAll(units, u => u.Symbols[0]))
                : null;

        /// <summary>
        /// The typed text in SI (decimal point) when it carries a unit known for the measure; <c>null</c> when it has no unit
        /// or the measure takes none (the text stays as typed). <paramref name="problem"/> tells a unit that does not suit.
        /// </summary>
        public static string? ToSi(string text, string? dataType, out string? problem)
        {
            problem = null;
            var match = Number.Match(text);
            if (!match.Success || !match.Groups[2].Success) return null;
            string symbol = match.Groups[2].Value;
            if (!(Measure(dataType) is { } measure) || !ByMeasure.TryGetValue(measure, out var units))
            {
                problem = Tr.T("unidade não reconhecida para este tipo: ") + symbol;
                return null;
            }

            foreach (var unit in units)
            {
                if (Array.IndexOf(unit.Symbols, symbol) < 0) continue;
                double value = double.Parse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);
                double si = value * unit.Factor + unit.Offset;
                return double.Parse(si.ToString("G12", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
            }

            problem = Tr.T("unidade não reconhecida: ") + symbol + Tr.T(" (aceitas: ") + Accepted(dataType) + ")";
            return null;
        }
    }
}
