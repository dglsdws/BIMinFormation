#if REVIT2027
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using BIMinFormation.Core.Mapping;
using BIMinFormation.Ids;

namespace BIMinFormation.Revit.Adapter
{
    /// <summary>
    /// Revit 2027: the exporter reads the user sets from the document (<see cref="IFCUserDefinedPropertySet"/>), not from
    /// the text file, and, through the export window, the set and quantity options come from a parameter template
    /// (<see cref="IFCParameterTemplate"/>) named in the configuration (<c>PropertyMapping</c>). revit-ifc code
    /// <c>Release_27.x.x</c>: <c>ExporterInitializer.InitUserDefinedPropertySets</c>, <c>PropertySetOptions.Create</c>,
    /// <c>IFCExportConfiguration.UpdateOptions</c>. Everything here needs an open transaction.
    /// </summary>
    public static class Revit2027Export
    {
        private static readonly Guid WrittenSchemaId = new Guid("5d0b7c2e-91a4-4f6b-8e3d-2a7c41b9e605");
        private const string WrittenField = "Names";

        /// <summary>
        /// Writes the sets of the PropertySets file to the document (one group per set; <c>null</c> = none). Sets with the
        /// same name are replaced (the Revit import would merge them or keep the last). The ones the plugin wrote in an earlier
        /// Export that left the mapping are deleted; the ones created by hand in the Revit window stay. Returns the names written.
        /// </summary>
        public static IReadOnlyList<string> WriteUserPropertySets(Document document, string? propertySetsFile)
        {
            var names = propertySetsFile == null
                ? new List<string>()
                : File.ReadLines(propertySetsFile)
                    .Select(l => l.TrimStart(' ', '\t').Split(new[] { '\t' }, StringSplitOptions.RemoveEmptyEntries))
                    .Where(f => f.Length >= 4 && string.Equals(f[0], "PropertySet:", StringComparison.OrdinalIgnoreCase))
                    .Select(f => f[1])
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
            foreach (string name in names.Concat(Written(document)).Distinct(StringComparer.Ordinal))
            {
                var existing = IFCUserDefinedPropertySet.FindPropertySetByName(document, name);
                if (existing != null) document.Delete(existing.Id);
            }

            if (propertySetsFile != null)
            {
                IFCUserDefinedPropertySet.ImportFromFile(document, propertySetsFile, out bool valid);
                if (!valid) throw new InvalidOperationException(Tr.T("O Revit recusou o arquivo de PropertySets: ") + propertySetsFile);
            }

            SaveWritten(document, names);
            return names;
        }

        /// <summary>Names of the sets written by the plugin in the last Export (document Extensible Storage).</summary>
        public static IReadOnlyList<string> Written(Document document)
        {
            var schema = Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(WrittenSchemaId);
            if (schema == null) return Array.Empty<string>();
            foreach (var storage in new FilteredElementCollector(document).OfClass(typeof(Autodesk.Revit.DB.ExtensibleStorage.DataStorage)))
            {
                var entity = storage.GetEntity(schema);
                if (entity != null && entity.IsValid())
                {
                    return entity.Get<string>(WrittenField).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                }
            }

            return Array.Empty<string>();
        }

        private static void SaveWritten(Document document, IReadOnlyList<string> names)
        {
            var schema = Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(WrittenSchemaId);
            if (schema == null)
            {
                var builder = new Autodesk.Revit.DB.ExtensibleStorage.SchemaBuilder(WrittenSchemaId);
                builder.SetSchemaName("BIMinFormationWrittenPropertySets");
                builder.AddSimpleField(WrittenField, typeof(string));
                schema = builder.Finish();
            }

            var storage = new FilteredElementCollector(document).OfClass(typeof(Autodesk.Revit.DB.ExtensibleStorage.DataStorage))
                              .Cast<Autodesk.Revit.DB.ExtensibleStorage.DataStorage>()
                              .FirstOrDefault(s => s.GetEntity(schema) is { } e && e.IsValid())
                          ?? Autodesk.Revit.DB.ExtensibleStorage.DataStorage.Create(document);
            var entity = new Autodesk.Revit.DB.ExtensibleStorage.Entity(schema);
            entity.Set(WrittenField, string.Join("\n", names));
            storage.SetEntity(entity);
        }

        /// <summary>Creates or updates the parameter template with the export options; returns its name.</summary>
        public static string SaveParameterTemplate(Document document, ExportSetup setup)
        {
            var template = IFCParameterTemplate.FindByName(document, setup.Name) ?? IFCParameterTemplate.Create(document, setup.Name);
            template.ExportIFCCommonPropertySets = setup.CommonPropertySets;
            template.ExportIFCBaseQuantities = setup.BaseQuantities;
            template.ExportRevitElementParameters = setup.InternalRevitPropertySets;
            template.ExportRevitMaterialParameters = false;
            template.ExportRevitSchedules = false;
            template.ExportUserDefinedPropertySets = setup.PropertySetsFile != null;
            Restrict(template, PropertySetupType.IfcCommonPropertySets, setup);
            Restrict(template, PropertySetupType.IfcBaseQuantities, setup);
            return template.Name;
        }

        /// <summary>
        /// "Only what the IDS asks": every standard set the exporter may write (the ones of the IFC 2x3, 4 and 4.3 catalogs, plus
        /// the ones already in the template) goes into the template, the sets outside <see cref="ExportSetup.RequestedSets"/>
        /// marked as not exported and, in the requested sets, the properties not asked for mapped as not exported. A template
        /// created by the API is empty (the export window is what fills it), and the exporter only leaves out what the template
        /// lists as not exported (revit-ifc <c>Release_27.x.x</c>, <c>ExporterInitializer.ExcludeNotExportingPropertySets</c> and
        /// <c>ExcludeNotExportingProperties</c>). With the option off, or a set named by a pattern, everything goes back to
        /// exported. Returns the sets and properties left out.
        /// </summary>
        public static (int Sets, int Properties) Restrict(IFCParameterTemplate template, PropertySetupType type, ExportSetup setup)
        {
            bool quantities = type == PropertySetupType.IfcBaseQuantities;
            var requested = setup.OnlyRequested ? setup.RequestedSets : null;
            var known = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var version in new[] { IfcSchemaVersion.Ifc2X3, IfcSchemaVersion.Ifc4, IfcSchemaVersion.Ifc4X3Add2 })
            {
                foreach (var set in IfcSchemaCatalog.For(version).PropertySets.Where(s => s.IsQuantitySet == quantities))
                {
                    if (!known.TryGetValue(set.Name, out var names)) known[set.Name] = names = new HashSet<string>(StringComparer.Ordinal);
                    names.UnionWith(set.Properties.Select(p => p.Name));
                }
            }

            foreach (string name in template.GetPropertySetNames(type, PropertySelectionType.All))
            {
                if (!known.ContainsKey(name)) known[name] = new HashSet<string>(StringComparer.Ordinal);
            }

            int sets = 0, properties = 0;
            foreach (var pair in known)
            {
                ISet<string>? asked = null;
                bool export = requested == null || requested.TryGetValue(pair.Key, out asked);
                if (!template.IsPropertySetAMemberOfTemplate(type, pair.Key))
                {
                    if (export && asked == null) continue; // not listed = exported with every property
                    template.AddPropertySet(type, export, pair.Key);
                }
                else if (template.IsExportingPropertySet(type, pair.Key) != export)
                {
                    template.SetPropertySetExportingFlag(type, pair.Key, export);
                }

                if (!export)
                {
                    sets++;
                    continue;
                }

                foreach (string property in pair.Value)
                {
                    bool keep = asked == null || asked.Contains(property);
                    var info = template.FindPropertyMappingInfo(type, pair.Key, property);
                    if (keep)
                    {
                        if (info != null && !info.ExportFlag) template.RemovePropertyMappingInfo(type, pair.Key, property);
                        continue;
                    }

                    properties++;
                    if (info != null && !info.ExportFlag) continue;
                    if (info != null) template.RemovePropertyMappingInfo(type, pair.Key, property);
                    template.AddPropertyMappingInfo(type, pair.Key, new IFCPropertyMappingInfo(false, property, ElementId.InvalidElementId, property));
                }
            }

            return (sets, properties);
        }
    }
}
#endif
