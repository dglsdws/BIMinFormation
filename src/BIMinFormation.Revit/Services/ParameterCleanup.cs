using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using BIMinFormation.Core.Fill;
using BIMinFormation.Exporter;
using BIMinFormation.Ids;
using BIMinFormation.Revit.Adapter;

namespace BIMinFormation.Revit.Services
{
    /// <summary>A parameter created by the plugin in the project.</summary>
    public sealed class CleanupCandidate
    {
        public ElementId Id { get; set; } = ElementId.InvalidElementId;
        public Guid Guid { get; set; }
        public string Name { get; set; } = "";

        /// <summary>The current mapping (specifications in use) uses the parameter.</summary>
        public bool InMapping => UsedBy.Count > 0;

        /// <summary>Specifications in use (applied to some element) that keep the parameter in the mapping.</summary>
        public IReadOnlyList<string> UsedBy { get; set; } = Array.Empty<string>();

        /// <summary>Elements and types with a value in the parameter.</summary>
        public int WithValue { get; set; }
    }

    /// <summary>
    /// Plugin parameter cleanup: after changing specifications or linking to Revit, parameters the mapping does not use are
    /// left over. The plugin's are the ones in its shared parameter file and the ones it would create for the specifications
    /// in the folder (deterministic GUID). Deleting removes the parameter and its values; the ones in the current mapping are excluded.
    /// </summary>
    public static class ParameterCleanup
    {
        public static List<CleanupCandidate> Find(Document document, PluginContext context)
        {
            var plugin = new HashSet<Guid>(FromParametersFile(context));
            foreach (var specification in context.Library.Specifications)
            {
                foreach (var facet in specification.Requirements.Facets.Concat(specification.Applicability.Facets).OfType<PropertyFacet>())
                {
                    if (!(facet.PropertySet is SimpleValue set) || !(facet.BaseName is SimpleValue name)) continue;
                    if (ValueOrigins.Classify(facet, context.SchemaCatalog, context.Exporter).Origin != ValueOrigin.CustomParameter) continue;
                    string type = DataTypeMap.Resolve(facet.DataType ?? "IFCLABEL", context.Exporter).PropertyType ?? "Label";
                    foreach (string variant in new[] { type, "Label" })
                    {
                        plugin.Add(ParameterGuid.For(set.Text, name.Text, variant, false));
                        plugin.Add(ParameterGuid.For(set.Text, name.Text, variant, true));
                    }
                }
            }

            var mapped = context.Mapping.Entities.SelectMany(context.Mapping.For).Where(p => p.ParameterGuid != null)
                .GroupBy(p => p.ParameterGuid!.Value)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(p => p.Specification).Distinct().OrderBy(s => s).ToList());
            var result = new List<CleanupCandidate>();
            foreach (var shared in new FilteredElementCollector(document).OfClass(typeof(SharedParameterElement)).Cast<SharedParameterElement>())
            {
                if (!plugin.Contains(shared.GuidValue)) continue;
                var filter = new ElementParameterFilter(ParameterFilterRuleFactory.CreateHasValueParameterRule(shared.Id));
                int withValue = new FilteredElementCollector(document).WhereElementIsNotElementType().WherePasses(filter).GetElementCount()
                                + new FilteredElementCollector(document).WhereElementIsElementType().WherePasses(filter).GetElementCount();
                result.Add(new CleanupCandidate
                {
                    Id = shared.Id,
                    Guid = shared.GuidValue,
                    Name = shared.Name,
                    UsedBy = mapped.TryGetValue(shared.GuidValue, out var usedBy) ? usedBy : Array.Empty<string>(),
                    WithValue = withValue,
                });
            }

            return result.OrderBy(c => c.InMapping).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        /// <summary>GUIDs in the plugin shared parameter file (PARAM lines).</summary>
        private static IEnumerable<Guid> FromParametersFile(PluginContext context)
        {
            string path = string.IsNullOrEmpty(context.Profile.ParametersFile) ? ParameterWriter.DefaultParametersFile : context.Profile.ParametersFile!;
            if (!File.Exists(path)) yield break;
            foreach (string line in File.ReadLines(path))
            {
                var fields = line.Split('\t');
                if (fields.Length > 2 && fields[0] == "PARAM" && Guid.TryParse(fields[1], out var guid)) yield return guid;
            }
        }

        /// <summary>Deletes the parameters (and values) in one transaction; never the ones in the current mapping.</summary>
        public static int Delete(Document document, IEnumerable<CleanupCandidate> candidates)
        {
            var ids = candidates.Where(c => !c.InMapping).Select(c => c.Id).ToList();
            if (ids.Count == 0) return 0;
            using var transaction = new Transaction(document, Tr.T("BIMinFormation: limpar parâmetros"));
            transaction.Start();
            document.Delete(ids);
            transaction.Commit();
            return ids.Count;
        }
    }
}
