using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using BIMinFormation.Core.Prevalidation;
using BIMinFormation.Ids;
using BIMinFormation.Revit.Adapter;
using BIMinFormation.Rules;
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

        /// <summary>
        /// Folder of the check IFC (recreated on every check), one per Revit process: two Revit open at the same time would
        /// otherwise delete and overwrite each other's file.
        /// </summary>
        public static string Folder => Path.Combine(Path.GetTempPath(), "BIMinFormation", "Conferencia",
            System.Diagnostics.Process.GetCurrentProcess().Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

        /// <summary>
        /// Model pre-validation with the IFC check on top. Returns <c>null</c> and the reason when this Revit does not export
        /// what the specifications require (the same warning as Export).
        /// </summary>
        public static PrevalidationResult? Run(Document document, PluginContext context, out string? problem, string? folder = null)
        {
            problem = null;
            if (folder == null)
            {
                folder = Folder;
                RemoveLeftovers();
            }

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
            Remember(document, ifc);
            Log.Info("Check in the IFC: " + path + " (" + ifc.Schema + ", " + ifc.Elements.Count + " elements read)");
            // the IFC only shows the property missing: the reason says it is the type conflict between specifications
            return PrevalidationService.ExplainConflicts(IfcProbe.Merge(model, ifc, path, DateTime.Now), context);
        }

        /// <summary>Check folders of Revit processes that are no longer running (best effort).</summary>
        private static void RemoveLeftovers()
        {
            try
            {
                foreach (string old in Directory.GetDirectories(Path.GetDirectoryName(Folder)!))
                {
                    if (!int.TryParse(Path.GetFileName(old), out int id)) continue;
                    bool running;
                    try
                    {
                        running = !System.Diagnostics.Process.GetProcessById(id).HasExited;
                    }
                    catch (ArgumentException)
                    {
                        running = false;
                    }

                    if (!running) Directory.Delete(old, true);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // a file still open somewhere: stays for the next check
            }
        }

        private static Document? _document;
        private static Dictionary<string, Dictionary<string, string>>? _quantities;

        /// <summary>The quantities of the last check (by GlobalId, then <c>Set/Property</c>), for Fill In Parameters.</summary>
        private static void Remember(Document document, ModelState ifc)
        {
            var quantities = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            var catalog = IfcSchemaCatalog.For(ifc.Schema);
            foreach (var element in ifc.Elements)
            {
                if (element.GlobalId == null) continue;
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var set in element.PropertySets.Where(s => s.Name.StartsWith("Qto_", StringComparison.Ordinal) || s.Name == "BaseQuantities"))
                {
                    foreach (var property in set.Properties)
                    {
                        var value = property.Values.FirstOrDefault(v => !v.IsEmpty);
                        if (value == null) continue;
                        // Fill In Parameters shows the value in the units it writes (kg for a weight), without the conversion noise
                        string text = IfcProbe.QuantityText(value, catalog.PropertySet(set.Name)?.Property(property.Name)?.DataType);
                        values[set.Name + "/" + property.Name] = text.EndsWith(" kg", StringComparison.Ordinal) ? text.Substring(0, text.Length - 3) : text;
                    }
                }

                quantities[element.GlobalId] = values;
            }

            _document = document;
            _quantities = quantities;
        }

        /// <summary>
        /// Value of the quantity the exporter wrote for the element in the last IFC check of this document (<c>null</c>
        /// when it did not), and whether the element came out in that IFC at all.
        /// </summary>
        public static string? Quantity(Document document, string? globalId, string set, string property, out bool exported)
        {
            exported = false;
            if (globalId == null || _quantities == null || _document == null || !_document.IsValidObject || !_document.Equals(document)) return null;
            if (!_quantities.TryGetValue(globalId, out var values)) return null;
            exported = true;
            return values.TryGetValue(set + "/" + property, out var value) ? value : null;
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
