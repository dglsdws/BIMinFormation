using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using BIMinFormation.Core.Mapping;
using BIMinFormation.Core.Profile;
using BIMinFormation.Revit.Adapter;
using BIMinFormation.Revit.Commands;
using BIMinFormation.Ids;

namespace BIMinFormation.Revit.Services
{
    /// <summary>
    /// Prepares the export through the Revit window: generates the PropertySets file in the format of the Revit in use and
    /// saves the "BIMinFormation (IDS)" configuration in the project.
    /// </summary>
    public static class ExportService
    {
        /// <summary>
        /// PropertySets file of the Export button: a single plugin file, rewritten on every click with the current project (the
        /// Revit window reads the file when exporting). Not the standalone file saved from Settings. One per Revit version: with
        /// two versions open, the Export of one would rewrite the file the other is about to export with.
        /// </summary>
        public static string PropertySetsPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BIMinFormation", "Exportacao",
                "BIMinFormation_PropertySets_" + (2000 + typeof(Document).Assembly.GetName().Version!.Major) + ".txt");

        /// <summary>
        /// Rewrites the values calculated by the plugin (derived link, e.g. the element material) that changed in the model,
        /// so the IFC comes out with the current value. Only those: the rest of Fill In Parameters stays with the user.
        /// </summary>
        public static int RefreshDerived(Document document, PluginContext context)
        {
            // the material and, in 2027, the links (copied to the plugin parameter)
            // and the quantity links (copied to the Qto parameter the exporter reads)
            if (!context.Profile.Mappings.Any(m => m.IsDerived || context.Format == MappingFormat.PerPropertySet
                                                   || m.PropertySet.StartsWith("Qto_", StringComparison.Ordinal))) return 0;
            var rows = FillService.BuildRows(document, context);
            foreach (var cell in rows.SelectMany(r => r.Cells).Where(c => !c.Derived))
            {
                cell.Typed = null;
            }

            if (!rows.SelectMany(r => r.Cells).Any(c => c.ToWrite())) return 0;
            string parameters = string.IsNullOrEmpty(context.Profile.ParametersFile) ? ParameterWriter.DefaultParametersFile : context.Profile.ParametersFile!;
            return FillService.Apply(document, context, rows, parameters).Written;
        }

        /// <summary>PropertySets file header: plugin version, date, specifications, schema, exporter and format.</summary>
        internal static string PropertySetsHeader(PluginContext context, int specifications, string origin = "") =>
            Tr.T("Arquivo de PropertySets gerado pelo BIMinFormation ") + App.Version + origin + " — "
            + DateTime.Now.ToString(Tr.T("yyyy-MM-dd HH:mm")) + Tr.T("\nFichas: ") + specifications
            + " (" + ProfileStatus.Name(context.Schema) + Tr.T("); exportador do Revit ") + context.Exporter.Year
            + Tr.T("\nFormato: ") + MappingFormats.Describe(context.Format);

        /// <summary>Parameter name in the project, found by GUID (parameters created earlier with another name).</summary>
        internal static string? ActualName(Document document, MappedProperty property) =>
            property.ParameterGuid == null ? null : SharedParameterElement.Lookup(document, property.ParameterGuid.Value)?.Name;

        public static ExportSetup Prepare(Document document, PluginContext context, string? propertySetsPath = null)
        {
            context = SpecificationUse.Refresh(document, context); // the mapping covers the specifications in use now
            var specifications = context.Profile.MappedSpecifications(context.Library.Specifications);
            var file = PropertySetsFile.From(context.Mapping, context.SchemaCatalog, p => ActualName(document, p));

            string? path = null;
            if (file.Sets.Count > 0)
            {
                path = propertySetsPath ?? PropertySetsPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            }

            var setup = ExportSetup.Build(specifications, context.Schema, context.Exporter, path, context.Profile.ExportOnlyRequested);
            setup.MappingWarnings.AddRange(file.Warnings);
            if (!setup.CanExport) return setup;

            RefreshDerived(document, context);

            if (path != null)
            {
                File.WriteAllBytes(path, file.ToBytes(PropertySetsHeader(context, specifications.Count, Tr.T(" (Exportar)"))));
            }

            using (var transaction = new Transaction(document, Tr.T("BIMinFormation: configuração de exportação IFC")))
            {
                transaction.Start();
#if REVIT2027
                // 2027: the user sets go to the document and the options to the configuration parameter template
                Adapter.Revit2027Export.WriteUserPropertySets(document, path); // no file: only removes the ones written before
                ExportConfiguration.Save(document, setup, Adapter.Revit2027Export.SaveParameterTemplate(document, setup));
#else
                ExportConfiguration.Save(document, setup);
#endif
                transaction.Commit();
            }

            return setup;
        }
    }
}
