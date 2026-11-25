using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using BIMinFormation.Core.Prevalidation;
using BIMinFormation.Ids;
using BIMinFormation.Revit.Adapter;
using BIMinFormation.Rules.Ifc;

namespace BIMinFormation.Revit.Services
{
    /// <summary>
    /// Check in the IFC: exports a temporary IFC with the same preparation as the Export button (derived values,
    /// PropertySets, configuration), reads the file and evaluates the specifications on it. Everything the preparation
    /// writes to the model is undone at the end (transaction group rolled back): the model does not change.
    /// </summary>
    public static class ProbeService
    {
        public const string FileName = "conferencia";

        /// <summary>Folder of the check IFC (recreated on every check).</summary>
        public static string Folder => Path.Combine(Path.GetTempPath(), "BIMinFormation", "Conferencia");

        /// <summary>
        /// Model pre-validation with the IFC check on top. Returns <c>null</c> and the reason when this Revit does not export
        /// what the specifications require (the same warning as Export).
        /// </summary>
        public static PrevalidationResult? Run(Document document, PluginContext context, out string? problem, string? folder = null)
        {
            problem = null;
            folder ??= Folder;
            Directory.CreateDirectory(folder);
            foreach (string old in Directory.GetFiles(folder, FileName + "*"))
            {
                try
                {
                    File.Delete(old);
                }
                catch (IOException)
                {
                    // file open in another program: the new export overwrites it or fails with the Revit message
                }
            }

            string path;
            using (var group = new TransactionGroup(document, Tr.T("BIMinFormation: conferência no IFC")))
            {
                group.Start();
                try
                {
                    var setup = ExportService.Prepare(document, context, Path.Combine(folder, FileName + "-PropertySets.txt"));
                    if (!setup.CanExport)
                    {
                        problem = string.Join("\n", setup.Problems);
                        return null;
                    }

                    var version = (IFCVersion)Enum.Parse(typeof(IFCVersion), setup.IfcVersion!);
                    path = IfcExport.Export(document, folder, FileName, version, setup.PropertySetsFile, setup.BaseQuantities);
                }
                finally
                {
                    group.RollBack();
                }
            }

            if (!File.Exists(path))
            {
                problem = Tr.T("O Revit não gerou o IFC de conferência.");
                return null;
            }

            // the model pre-validation after undoing the preparation: it is what the IFC goes on top of
            context = PluginContext.Create(document, out _) ?? context;
            var model = PrevalidationService.Run(document, context);
            var ifc = IfcModelReader.Read(path, includeEntity: ApplicableEntities(context.Library.Specifications));
            Log.Info("Check in the IFC: " + path + " (" + ifc.Schema + ", " + ifc.Elements.Count + " elements read)");
            // the IFC only shows the property missing: the reason says it is the type conflict between specifications
            return PrevalidationService.ExplainConflicts(IfcProbe.Merge(model, ifc, path, DateTime.Now), context);
        }

        /// <summary>
        /// Entities in the specification applicabilities: besides the elements (always read), the reader takes the instances of
        /// these entities (e.g. a specification about <c>IfcMaterial</c>).
        /// </summary>
        private static Func<string, bool> ApplicableEntities(IEnumerable<Specification> specifications)
        {
            var names = new HashSet<string>(specifications.SelectMany(s => s.Applicability.Facets.OfType<EntityFacet>())
                .Select(f => (f.Name as SimpleValue)?.Text.ToUpperInvariant()).Where(n => n != null)!, StringComparer.Ordinal);
            return names.Contains;
        }
    }
}
