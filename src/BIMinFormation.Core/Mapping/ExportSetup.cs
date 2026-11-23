using System.Collections.Generic;
using System.Linq;
using BIMinFormation.Exporter;
using BIMinFormation.Ids;

namespace BIMinFormation.Core.Mapping
{
    /// <summary>
    /// Revit IFC export window configuration built from the specifications (the same decisions as
    /// <see cref="ExportChecklist"/>): IFC version, base quantities, standard sets and PropertySets file. The Export button
    /// saves this configuration in the project and opens the Revit window with it; Revit does the export.
    /// </summary>
    public sealed class ExportSetup
    {
        /// <summary>Configuration name in the export window list.</summary>
        public const string DefaultName = "BIMinFormation (IDS)";

        public string Name { get; private set; } = DefaultName;

        /// <summary>Name in the <c>IFCVersion</c> enum (e.g. <c>IFC4DTV</c>); <c>null</c> when this Revit does not deliver the schema.</summary>
        public string? IfcVersion { get; private set; }

        public bool BaseQuantities { get; private set; }
        public bool CommonPropertySets { get; private set; } = true;
        public bool InternalRevitPropertySets { get; private set; }

        /// <summary>User PropertySets file; <c>null</c> when the specifications only ask for standard sets.</summary>
        public string? PropertySetsFile { get; private set; }

        /// <summary>Reasons not to export (e.g. IFC 4.3 in Revit 2023).</summary>
        public List<string> Problems { get; } = new List<string>();

        /// <summary>Mapping warnings: requirements the PropertySets file cannot meet (type conflict, no output).</summary>
        public List<string> MappingWarnings { get; } = new List<string>();

        public bool CanExport => IfcVersion != null && Problems.Count == 0;

        public static ExportSetup Build(IEnumerable<Specification> specifications, IfcSchemaVersion schema,
            ExporterCatalog exporter, string? propertySetsFile)
        {
            var specs = specifications.ToList();
            var catalog = IfcSchemaCatalog.For(schema);
            var origins = specs.SelectMany(s => s.Requirements.Facets)
                .Select(f => ValueOrigins.Classify(f, catalog, exporter).Origin)
                .ToList();
            var setup = new ExportSetup
            {
                BaseQuantities = origins.Contains(ValueOrigin.ExporterQuantity),
                PropertySetsFile = propertySetsFile,
            };

            string? version = schema switch
            {
                IfcSchemaVersion.Ifc2X3 => "IFC2x3CV2",
                // the Reference View drops sets outside the certified list (Pset_DoorWindowGlazingType…)
                IfcSchemaVersion.Ifc4 => "IFC4DTV",
                // 2026+: IFC4x3 writes IFC4X3_ADD2 with quantities; 2025: only the Reference View (no quantities);
                // 2023.1/2024: experimental (IFC4X3 header)
                IfcSchemaVersion.Ifc4X3Add2 when exporter.Year >= 2025 => "IFC4x3",
                _ => null,
            };

            if (version == null)
            {
                setup.Problems.Add(Tr.T("As fichas pedem IFC 4.3 (IFC4X3_ADD2), que o Revit ") + exporter.Year + Tr.T(" não entrega: ")
                                   + (exporter.CanExport(schema)
                                       ? Tr.T("o 4.3 experimental grava o cabeçalho IFC4X3 e não exporta quantidades.")
                                       : Tr.T("esta versão não tem IFC 4.3."))
                                   + Tr.T(" Exporte do Revit 2026 ou mais novo."));
            }
            else if (schema == IfcSchemaVersion.Ifc4X3Add2 && setup.BaseQuantities && !exporter.Ifc4x3WithQuantities)
            {
                setup.Problems.Add(Tr.T("As fichas pedem quantidades (Qto_), e o Revit ") + exporter.Year + Tr.T(" exporta IFC 4.3 só na Vista ")
                                   + Tr.T("de Referência, que não grava quantidades. Exporte do Revit 2026 ou mais novo (Vista de ")
                                   + Tr.T("Transferência de Projeto IFC4x3)."));
                version = null;
            }
            else if (!exporter.IfcVersions.Contains(version))
            {
                setup.Problems.Add(Tr.T("O exportador do Revit ") + exporter.Year + Tr.T(" não tem a opção ") + version + ".");
                version = null;
            }

            setup.IfcVersion = version;
            return setup;
        }
    }
}
