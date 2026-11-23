using System;
using System.IO;
using Autodesk.Revit.DB;
using BIMinFormation.Ids;

namespace BIMinFormation.Revit.Adapter
{
    /// <summary>
    /// IFC export with the options the checklist asks for. Exporting changes the document temporarily, so it runs in a
    /// transaction that is rolled back at the end: the model does not change.
    /// </summary>
    public static class IfcExport
    {
        public static string Export(Document document, string folder, string name, IFCVersion version,
            string? propertySetsFile, bool baseQuantities = true)
        {
            Directory.CreateDirectory(folder);
            var options = new IFCExportOptions
            {
                FileVersion = version,
                ExportBaseQuantities = baseQuantities,
                WallAndColumnSplitting = false,
            };
            options.AddOption("ExportBaseQuantities", baseQuantities ? "true" : "false"); // 2027 without a named template reads it from here
            options.AddOption("ExportIFCCommonPropertySets", "true");
            options.AddOption("ExportInternalRevitPropertySets", "false");
            if (propertySetsFile != null)
            {
                options.AddOption("ExportUserDefinedPsets", "true");
                options.AddOption("ExportUserDefinedPsetsFileName", propertySetsFile);
            }

            return Export(document, folder, name, options, prepare: document =>
            {
#if REVIT2026_OR_GREATER
                // 2026: the exporter ignores these three options and reads quantities, common sets and Revit sets from the session
                // parameter template, which the export window fills from the configuration (2027 without a named template reads the
                // options again; filling it does no harm)
                var template = IFCParameterTemplate.GetOrCreateInSessionTemplate(document);
                template.ExportIFCBaseQuantities = baseQuantities;
                template.ExportIFCCommonPropertySets = true;
                template.ExportRevitElementParameters = false;
#endif
#if REVIT2027
                // 2027: the user sets come from the document; written only during the export (the transaction is rolled back)
                if (propertySetsFile != null) Revit2027Export.WriteUserPropertySets(document, propertySetsFile);
#endif
            });
        }

        public static string Export(Document document, string folder, string name, IFCExportOptions options,
            Action<Document>? prepare = null)
        {
            Directory.CreateDirectory(folder);
            using (var transaction = new Transaction(document, Tr.T("BIMinFormation: exportação IFC")))
            {
                transaction.Start();
                prepare?.Invoke(document);
                document.Export(folder, name, options);
                transaction.RollBack();
            }

            return Path.Combine(folder, name.EndsWith(".ifc") ? name : name + ".ifc");
        }
    }
}
