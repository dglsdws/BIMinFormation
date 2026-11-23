using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BIMinFormation.Core.Profile;
using BIMinFormation.Exporter;
using BIMinFormation.Ids;

namespace BIMinFormation.Core.Mapping
{
    /// <summary>A <c>PropertySet:</c> group of the file (set + instance/type + entities with the same properties).</summary>
    public sealed class MappedPropertySet
    {
        public string Name { get; }
        public ParameterScope Scope { get; }
        public IReadOnlyList<string> Entities { get; }
        public IReadOnlyList<MappedProperty> Properties { get; }

        public MappedPropertySet(string name, ParameterScope scope, IReadOnlyList<string> entities, IReadOnlyList<MappedProperty> properties)
        {
            Name = name;
            Scope = scope;
            Entities = entities;
            Properties = properties;
        }
    }

    /// <summary>
    /// User PropertySets file for the Revit IFC exporter: UTF-8 without BOM, fields separated by TAB only, groups by set
    /// and scope (<c>I</c> instance, <c>T</c> type). Entities with the same properties (and the same types) share a group;
    /// when the same property has different types in different entities, each type goes in a group with its own parameter
    /// (<see cref="ExportMapping"/>). Only user sets go in: the standard Psets/Qtos come from the parameters Revit installs.
    /// </summary>
    public sealed class PropertySetsFile
    {
        public const string FallbackEntity = ExportMapping.FallbackEntity;

        public IReadOnlyList<MappedPropertySet> Sets { get; }

        /// <summary>Warnings for the summary (conflicting types in an entity, types without an equivalent, specifications without an entity).</summary>
        public IReadOnlyList<string> Warnings { get; }

        public ExportMapping Mapping { get; }

        private PropertySetsFile(IReadOnlyList<MappedPropertySet> sets, ExportMapping mapping)
        {
            Sets = sets;
            Mapping = mapping;
            Warnings = mapping.Warnings;
        }

        public static PropertySetsFile Generate(IEnumerable<Specification> specifications, IfcSchemaVersion schema,
            ExporterCatalog exporter, ProjectProfile profile, MappingFormat format = MappingFormat.PerEntity) =>
            From(ExportMapping.Build(specifications, schema, exporter, profile, format), IfcSchemaCatalog.For(schema));

        /// <param name="actualName">
        /// Name the parameter already has in the project (found by GUID), when it differs from the expected one: the file
        /// uses the actual name, or the exporter would not find the parameter.
        /// </param>
        public static PropertySetsFile From(ExportMapping mapping, IfcSchemaCatalog catalog, Func<MappedProperty, string?>? actualName = null)
        {
            // global property order: the order of first appearance in the specifications
            var order = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string entity in mapping.Entities)
            {
                foreach (var property in mapping.For(entity))
                {
                    string key = property.PropertySet + "\t" + property.Name + "\t" + property.Scope + "\t" + property.PropertyType;
                    if (!order.ContainsKey(key)) order[key] = order.Count;
                }
            }

            var groups = new List<(string Set, ParameterScope Scope, string Signature, List<string> Entities, List<MappedProperty> Properties)>();
            foreach (string entity in mapping.Entities)
            {
                foreach (var bySet in mapping.For(entity).GroupBy(p => (p.PropertySet, p.Scope)))
                {
                    var properties = bySet.OrderBy(p => order[p.PropertySet + "\t" + p.Name + "\t" + p.Scope + "\t" + p.PropertyType])
                        .Select(p => actualName?.Invoke(p) is { } real && real != p.Parameter ? p.WithParameter(real) : p)
                        .ToList();
                    // 2023 to 2026: one group per set of entities with the same properties; 2027: one group per property set
                    bool perSet = mapping.Format == MappingFormat.PerPropertySet;
                    string signature = perSet ? "" : string.Join("\n", properties.Select(p => p.Signature));
                    var group = groups.FirstOrDefault(g => g.Set == bySet.Key.PropertySet && g.Scope == bySet.Key.Scope && g.Signature == signature);
                    string name = bySet.Key.Scope == ParameterScope.Type ? TypeEntity(entity, catalog) : entity;
                    if (group.Entities == null)
                    {
                        groups.Add((bySet.Key.PropertySet, bySet.Key.Scope, signature, new List<string> { name }, properties));
                    }
                    else
                    {
                        if (!group.Entities.Contains(name)) group.Entities.Add(name);
                        if (perSet)
                        {
                            group.Properties.AddRange(properties.Where(p => !group.Properties.Any(x => x.Name == p.Name)));
                        }
                    }
                }
            }

            var sets = groups
                .Select(g => new MappedPropertySet(g.Set, g.Scope, g.Entities.OrderBy(e => e, StringComparer.Ordinal).ToList(),
                    g.Properties.OrderBy(p => order[p.PropertySet + "\t" + p.Name + "\t" + p.Scope + "\t" + p.PropertyType]).ToList()))
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .ThenBy(s => s.Scope)
                .ThenBy(s => s.Entities[0], StringComparer.Ordinal)
                .ToList();
            return new PropertySetsFile(sets, mapping);
        }

        public string ToText(string header)
        {
            var text = new StringBuilder();
            foreach (string line in header.Split('\n'))
            {
                text.Append("# ").Append(line.TrimEnd('\r')).Append("\r\n");
            }

            foreach (var set in Sets)
            {
                text.Append("#\r\n");
                text.Append("PropertySet:\t").Append(set.Name).Append('\t')
                    .Append(set.Scope == ParameterScope.Type ? "T" : "I").Append('\t')
                    .Append(string.Join(",", set.Entities)).Append("\r\n");
                foreach (var property in set.Properties)
                {
                    text.Append('\t').Append(property.Name).Append('\t').Append(property.PropertyType).Append('\t')
                        .Append(property.Parameter).Append("\r\n");
                }
            }

            return text.ToString();
        }

        public byte[] ToBytes(string header) => new UTF8Encoding(false).GetBytes(ToText(header));

        /// <summary>Entities (spelled as in the schema) required by the specification, in the requirements or the applicability.</summary>
        internal static List<string> Entities(Specification specification, IfcSchemaCatalog catalog)
        {
            var names = specification.Requirements.Facets.Concat(specification.Applicability.Facets)
                .OfType<EntityFacet>()
                .SelectMany(e => e.Name switch
                {
                    SimpleValue simple => new[] { simple.Text },
                    RestrictionValue restriction => restriction.Enumerations.ToArray(),
                    _ => new string[0],
                });
            return names
                .Select(n => catalog.Entity(n)?.Name ?? n)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        private static string TypeEntity(string entity, IfcSchemaCatalog catalog)
        {
            var type = catalog.Entity(entity + "Type") ?? catalog.Entity(entity + "Style");
            return type?.Name ?? entity;
        }
    }
}
