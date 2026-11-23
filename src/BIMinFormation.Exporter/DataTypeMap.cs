using System;
using System.Collections.Generic;
using BIMinFormation.Ids;

namespace BIMinFormation.Exporter
{
    /// <summary>
    /// How an IDS <c>dataType</c> becomes a Revit parameter and a PropertySets file line: the exporter type (enum
    /// <c>PropertyType</c>) and the parameter spec in Revit (<c>SpecTypeId</c> property name, e.g. <c>Length</c>,
    /// <c>Boolean.YesNo</c>).
    /// </summary>
    public sealed class ExporterDataType
    {
        public string IdsDataType { get; }

        /// <summary>Name in the exporter <c>PropertyType</c> enum; <c>null</c> when the version does not have it.</summary>
        public string? PropertyType { get; }

        /// <summary><c>SpecTypeId</c> property used to create the parameter; <c>null</c> when there is none.</summary>
        public string? RevitSpec { get; }

        /// <summary>Warning for the user (type without an equivalent, ambiguous spec…).</summary>
        public string? Note { get; }

        public ExporterDataType(string idsDataType, string? propertyType, string? revitSpec, string? note)
        {
            IdsDataType = idsDataType;
            PropertyType = propertyType;
            RevitSpec = revitSpec;
            Note = note;
        }

        /// <summary>Can be written in Revit and exported with the right type.</summary>
        public bool IsSupported => PropertyType != null && RevitSpec != null;

        public override string ToString() => IdsDataType + " → " + (PropertyType ?? "?") + " / " + (RevitSpec ?? "?");
    }

    public static class DataTypeMap
    {
        /// <summary>
        /// Revit spec for each exporter type. Quantities that belong to more than one discipline in Revit (power, flow,
        /// temperature, pressure, velocity) use the HVAC or electrical one, as indicated; the user can change it in the profile.
        /// </summary>
        private static readonly Dictionary<string, string> Specs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Label"] = "String.Text",
            ["Text"] = "String.Text",
            ["Identifier"] = "String.Text",
            ["Date"] = "String.Text",
            ["DateTime"] = "String.Text",
            ["LocalTime"] = "String.Text",
            ["Duration"] = "String.Text",
            ["Boolean"] = "Boolean.YesNo",
            ["Logical"] = "Boolean.YesNo",
            ["Integer"] = "Int.Integer",
            ["Count"] = "Int.Integer",
            ["Real"] = "Number",
            ["Numeric"] = "Number",
            ["Ratio"] = "Number",
            ["PositiveRatio"] = "Number",
            ["NormalisedRatio"] = "Number",
            ["Length"] = "Length",
            ["PositiveLength"] = "Length",
            ["NonNegativeLength"] = "Length",
            ["Area"] = "Area",
            ["Volume"] = "Volume",
            ["PlaneAngle"] = "Angle",
            ["PositivePlaneAngle"] = "Angle",
            ["Mass"] = "Mass",
            ["MassDensity"] = "MassDensity",
            ["Power"] = "ElectricalPower",
            ["ElectricVoltage"] = "ElectricalPotential",
            ["ElectricCurrent"] = "Current",
            ["ElectricResistance"] = "ElectricalResistivity",
            ["Frequency"] = "ElectricalFrequency",
            ["Illuminance"] = "Illuminance",
            ["LuminousFlux"] = "LuminousFlux",
            ["LuminousIntensity"] = "LuminousIntensity",
            ["ColorTemperature"] = "ColorTemperature",
            ["ElectricalEfficacy"] = "Efficacy",
            ["VolumetricFlowRate"] = "AirFlow",
            ["ThermodynamicTemperature"] = "HvacTemperature",
            ["ThermalTransmittance"] = "HeatTransferCoefficient",
            ["ThermalConductivity"] = "ThermalConductivity",
            ["ThermalResistance"] = "ThermalResistance",
            ["Pressure"] = "HvacPressure",
            ["LinearVelocity"] = "HvacVelocity",
            ["Force"] = "Force",
            ["LinearForce"] = "LinearForce",
            ["PlanarForce"] = "AreaForce",
            ["Energy"] = "Energy",
            ["SpecificHeatCapacity"] = "SpecificHeat",
            ["ModulusOfElasticity"] = "Stress",
            ["Currency"] = "Currency",
            ["Monetary"] = "Currency",
            ["Time"] = "Time",
            ["MassFlowRate"] = "PipingMassPerTime",
            ["MassPerLength"] = "MassPerUnitLength",
            ["AreaDensity"] = "MassPerUnitArea",
            ["MomentOfInertia"] = "MomentOfInertia",
            ["SectionModulus"] = "SectionModulus",
            ["WarpingConstant"] = "WarpingConstant",
            ["SoundPower"] = "Number",
            ["SoundPressure"] = "Number",
        };

        private static readonly Dictionary<string, string> AmbiguousNotes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Power"] = Tr.T("potência elétrica (troque para HvacPower no perfil se for climatização)"),
            ["VolumetricFlowRate"] = Tr.T("vazão de ar (troque para Flow no perfil se for hidráulica)"),
            ["ThermodynamicTemperature"] = Tr.T("temperatura de HVAC"),
            ["Pressure"] = Tr.T("pressão de HVAC"),
            ["LinearVelocity"] = Tr.T("velocidade de HVAC"),
            ["SoundPower"] = Tr.T("sem unidade no Revit: o valor é gravado como número em watts"),
            ["SoundPressure"] = Tr.T("sem unidade no Revit: o valor é gravado como número em pascal"),
        };

        /// <summary>
        /// <c>IFCPOSITIVELENGTHMEASURE</c> → <c>PositiveLength</c>; <c>IFCLABEL</c> → <c>Label</c>. The name must exist in the
        /// <c>PropertyType</c> enum of the version: the exporter silently turns a type it does not know into text.
        /// </summary>
        public static ExporterDataType Resolve(string idsDataType, ExporterCatalog catalog)
        {
            string upper = idsDataType.ToUpperInvariant();
            string core = upper.StartsWith("IFC", StringComparison.Ordinal) ? upper.Substring(3) : upper;
            string? propertyType = Find(core, catalog);
            if (propertyType == null && core.EndsWith("MEASURE", StringComparison.Ordinal))
            {
                propertyType = Find(core.Substring(0, core.Length - "MEASURE".Length), catalog);
            }

            if (propertyType == null)
            {
                return new ExporterDataType(upper, null, null,
                    Tr.T("O exportador do Revit ") + catalog.Year + Tr.T(" não tem tipo para ") + upper
                    + Tr.T("; a propriedade sairia como texto e reprovaria. Comunique o contratante."));
            }

            Specs.TryGetValue(propertyType, out string? spec);
            AmbiguousNotes.TryGetValue(propertyType, out string? note);
            if (spec == null)
            {
                note = Tr.T("O Revit não tem especificação para ") + propertyType + ".";
            }

            return new ExporterDataType(upper, propertyType, spec, note);
        }

        private static string? Find(string name, ExporterCatalog catalog)
        {
            foreach (string candidate in catalog.PropertyTypes)
            {
                if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }

            return null;
        }
    }
}
