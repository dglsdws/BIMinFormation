using System;
using System.Collections.Generic;
using System.Reflection;
using Autodesk.Revit.DB;

namespace BIMinFormation.Revit.Adapter
{
    /// <summary>
    /// Specs (<see cref="SpecTypeId"/>) by the name used in the catalog (<c>Length</c>, <c>Boolean.YesNo</c>) and the SI
    /// unit of each, to convert between Revit internal units and the IDS units.
    /// </summary>
    internal static class RevitSpecs
    {
        private static readonly Dictionary<string, ForgeTypeId> SiUnits = new Dictionary<string, ForgeTypeId>(StringComparer.Ordinal);

        static RevitSpecs()
        {
            void Add(ForgeTypeId spec, ForgeTypeId unit) => SiUnits[spec.TypeId] = unit;
            Add(SpecTypeId.Length, UnitTypeId.Meters);
            Add(SpecTypeId.Area, UnitTypeId.SquareMeters);
            Add(SpecTypeId.Volume, UnitTypeId.CubicMeters);
            Add(SpecTypeId.Angle, UnitTypeId.Radians);
            Add(SpecTypeId.Mass, UnitTypeId.Kilograms);
            Add(SpecTypeId.MassDensity, UnitTypeId.KilogramsPerCubicMeter);
            Add(SpecTypeId.ElectricalPower, UnitTypeId.Watts);
            Add(SpecTypeId.HvacPower, UnitTypeId.Watts);
            Add(SpecTypeId.ElectricalPotential, UnitTypeId.Volts);
            Add(SpecTypeId.Current, UnitTypeId.Amperes);
            Add(SpecTypeId.ElectricalFrequency, UnitTypeId.Hertz);
            Add(SpecTypeId.AirFlow, UnitTypeId.CubicMetersPerSecond);
            Add(SpecTypeId.Flow, UnitTypeId.CubicMetersPerSecond);
            Add(SpecTypeId.HvacTemperature, UnitTypeId.Kelvin);
            Add(SpecTypeId.HvacPressure, UnitTypeId.Pascals);
            Add(SpecTypeId.HvacVelocity, UnitTypeId.MetersPerSecond);
            Add(SpecTypeId.Force, UnitTypeId.Newtons);
            Add(SpecTypeId.Energy, UnitTypeId.Joules);
            Add(SpecTypeId.HeatTransferCoefficient, UnitTypeId.WattsPerSquareMeterKelvin);
            Add(SpecTypeId.ThermalConductivity, UnitTypeId.WattsPerMeterKelvin);
        }

        /// <summary><c>Length</c> → SpecTypeId.Length; <c>Boolean.YesNo</c> → SpecTypeId.Boolean.YesNo.</summary>
        public static ForgeTypeId? Resolve(string name)
        {
            Type? type = typeof(SpecTypeId);
            string[] parts = name.Split('.');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                type = type.GetNestedType(parts[i], BindingFlags.Public);
                if (type == null)
                {
                    return null;
                }
            }

            var property = type.GetProperty(parts[parts.Length - 1], BindingFlags.Public | BindingFlags.Static);
            return property?.GetValue(null) as ForgeTypeId;
        }

        public static ForgeTypeId? SiUnit(ForgeTypeId spec) =>
            SiUnits.TryGetValue(spec.TypeId, out var unit) ? unit : null;

        /// <summary>
        /// Revit internal value → SI (or the value itself, when the spec has no unit), rounded to 12 significant digits: the
        /// round trip through feet leaves noise in the last digits (0.1 m came back as 0.100000000000002), far below the IDS
        /// tolerance (1e-6 relative).
        /// </summary>
        public static double ToSi(double internalValue, ForgeTypeId spec)
        {
            var unit = SiUnit(spec);
            return Clean(unit == null ? internalValue : UnitUtils.ConvertFromInternalUnits(internalValue, unit));
        }

        /// <summary>Rounds to 12 significant digits (keeps 0, infinities and NaN as they are).</summary>
        public static double Clean(double value) =>
            value == 0 || double.IsNaN(value) || double.IsInfinity(value)
                ? value
                : double.Parse(value.ToString("G12", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);

        public static double FromSi(double siValue, ForgeTypeId spec)
        {
            var unit = SiUnit(spec);
            return unit == null ? siValue : UnitUtils.ConvertToInternalUnits(siValue, unit);
        }
    }
}
