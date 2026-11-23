using System;
using System.Collections.Generic;
using System.Linq;
using BIMinFormation.Core.Profile;
using BIMinFormation.Exporter;
using BIMinFormation.Ids;

namespace BIMinFormation.Core.Mapping
{
    /// <summary>How the Revit IFC exporter reads the user property sets.</summary>
    public enum MappingFormat
    {
        /// <summary>
        /// Revit 2023 to 2026: the exporter reads the PropertySets file when exporting (<c>PropertyMap.LoadUserDefinedPset</c>,
        /// the same from 2023 to 2026 in the revit-ifc code) and accepts several groups with the same name; one group per
        /// entity, each with the types of its specifications.
        /// </summary>
        PerEntity,

        /// <summary>
        /// Revit 2027: the new mapping keeps the sets in the document (<c>IFCParameterTemplate</c>), one per name, with a single
        /// list of entities; importing several groups with the same name keeps only the last one (checked in Revit 2027). One
        /// group per set, with a single type per property.
        /// </summary>
        PerPropertySet,
    }

    public static class MappingFormats
    {
        /// <summary>Format of the Revit exporter of a given year (2027 keeps the sets in the document).</summary>
        public static MappingFormat ForRevitYear(int year) => year >= 2027 ? MappingFormat.PerPropertySet : MappingFormat.PerEntity;

        public static string Describe(MappingFormat format) =>
            format == MappingFormat.PerPropertySet
                ? Tr.T("Revit 2027 (um grupo por conjunto)")
                : Tr.T("Revit 2023 a 2026 (um grupo por entidade)");
    }

    /// <summary>A user property set property as the exporter will write it for an entity.</summary>
    public sealed class MappedProperty
    {
        public string PropertySet { get; }
        public string Name { get; }

        /// <summary>Exporter type (enum <c>PropertyType</c>), e.g. <c>Label</c>, <c>Length</c>.</summary>
        public string PropertyType { get; }

        /// <summary>Revit parameter read by the exporter (name, or <c>BuiltInParameter.XXX</c>).</summary>
        public string Parameter { get; }

        public ParameterScope Scope { get; }

        /// <summary><c>dataType</c> required by the IDS (<c>null</c> when the IDS does not say).</summary>
        public string? IdsDataType { get; }

        /// <summary>First specification that asked for the property with this type.</summary>
        public string Specification { get; }

        /// <summary>
        /// GUID of the plugin shared parameter (deterministic); <c>null</c> when the profile maps the property to an existing
        /// parameter (link).
        /// </summary>
        public Guid? ParameterGuid { get; }

        public MappedProperty(string propertySet, string name, string propertyType, string parameter, ParameterScope scope,
            string? idsDataType, string specification, Guid? parameterGuid = null)
        {
            PropertySet = propertySet;
            Name = name;
            PropertyType = propertyType;
            Parameter = parameter;
            Scope = scope;
            IdsDataType = idsDataType;
            Specification = specification;
            ParameterGuid = parameterGuid;
        }

        /// <summary>The same property with another parameter name (the actual name in the project).</summary>
        public MappedProperty WithParameter(string parameter) =>
            new MappedProperty(PropertySet, Name, PropertyType, parameter, Scope, IdsDataType, Specification, ParameterGuid);

        internal string Signature => PropertySet + "\t" + Name + "\t" + PropertyType + "\t" + Parameter;
    }

    /// <summary>
    /// What the exporter must write, per entity, for the user property sets of the active specifications. The same
    /// property may have different types in different specifications (in BIMPR, <c>Comprimento</c> is a length in
    /// PR.05.25 DRENO, a proxy, and text in PR.15.15 MOBILIARIO, an IfcFurniture): each entity gets the type of its
    /// specifications and each type has its own parameter. The type used most across the whole set keeps the simple name
    /// (<c>BIMPR.Comprimento</c>); the others carry the type in the name (<c>BIMPR.Comprimento (Label)</c>).
    /// <para>
    /// Within one entity the Revit exporter writes a single type per property (repeated lines collapse into one, with the
    /// type of the last; checked in Revit 2023 with <c>Diametro</c> of DRENO and HASTE, both proxies). There the type asked
    /// by most specifications of the entity wins, and the other specifications get a warning: they will fail on that property.
    /// </para>
    /// </summary>
    public sealed class ExportMapping
    {
        private readonly Dictionary<string, List<MappedProperty>> _byEntity;
        private readonly HashSet<string> _customPairs;
        private readonly HashSet<string> _clashes;

        /// <summary>Entities spelled as in the schema, in the order they appear in the specifications.</summary>
        public IReadOnlyList<string> Entities { get; }

        public IReadOnlyList<string> Warnings { get; }

        /// <summary>Property → type with the simple name (the most used), for properties with more than one type.</summary>
        public IReadOnlyDictionary<string, string> DominantTypes { get; }

        public MappingFormat Format { get; }

        private ExportMapping(MappingFormat format, Dictionary<string, List<MappedProperty>> byEntity, IReadOnlyList<string> entities,
            IReadOnlyList<string> warnings, IReadOnlyDictionary<string, string> dominant, HashSet<string> customPairs,
            HashSet<string> clashes)
        {
            Format = format;
            _byEntity = byEntity;
            Entities = entities;
            Warnings = warnings;
            DominantTypes = dominant;
            _customPairs = customPairs;
            _clashes = clashes;
        }

        /// <summary>Entity used when the specification does not say which (falls back to a proxy).</summary>
        public const string FallbackEntity = "IfcBuildingElementProxy";

        public IReadOnlyList<MappedProperty> For(string entity) =>
            _byEntity.TryGetValue(entity, out var list) ? list : (IReadOnlyList<MappedProperty>)new MappedProperty[0];

        /// <summary>The property variant for the entity: the one with the requested type, or the first when no type is given.</summary>
        public MappedProperty? Find(string entity, string propertySet, string property, string? propertyType = null) =>
            For(entity).FirstOrDefault(p => p.PropertySet == propertySet && p.Name == property
                                            && (propertyType == null || p.PropertyType == propertyType));

        /// <summary>
        /// Parameter name for the property with this type: with the type in the name when it is not the most used one, or when
        /// Revit already has a standard parameter with the simple name (standard Pset exported in a user set).
        /// </summary>
        public string NameFor(string propertySet, string property, string propertyType, ParameterScope scope) =>
            ParameterName(propertySet, property,
                _clashes.Contains(propertySet + "." + property)
                || DominantTypes.TryGetValue(propertySet + "." + property, out var dominant) && dominant != propertyType
                    ? propertyType
                    : null,
                scope);

        /// <summary>The property belongs to a user set in some active specification.</summary>
        public bool IsCustom(string propertySet, string property) => _customPairs.Contains(propertySet + "\t" + property);

        public static ExportMapping Build(IEnumerable<Specification> specifications, IfcSchemaVersion schema, ExporterCatalog exporter,
            ProjectProfile profile, MappingFormat format = MappingFormat.PerEntity)
        {
            var catalog = IfcSchemaCatalog.For(schema);
            var warnings = new List<string>();
            var requests = new List<(string Spec, List<string> Entities, string Set, string Name, string Type, string? IdsType, ParameterScope Scope)>();

            foreach (var specification in specifications)
            {
                var specEntities = PropertySetsFile.Entities(specification, catalog);
                if (specEntities.Count == 0)
                {
                    warnings.Add(specification.Name + Tr.T(": a ficha não define a entidade; o conjunto vai para ") + FallbackEntity + ".");
                    specEntities = new List<string> { FallbackEntity };
                }

                foreach (var facet in specification.Applicability.Facets.Concat(specification.Requirements.Facets).OfType<PropertyFacet>())
                {
                    if (!(facet.PropertySet is SimpleValue pset) || !(facet.BaseName is SimpleValue name))
                    {
                        continue;
                    }

                    var origin = ValueOrigins.Classify(facet, catalog, exporter);
                    if (origin.Origin == ValueOrigin.NotExportable && (origin.DataType != null || pset.Text.StartsWith("Pset_", StringComparison.Ordinal)
                                                                       || pset.Text.StartsWith("Qto_", StringComparison.Ordinal)))
                    {
                        warnings.Add(specification.Name + ": " + pset.Text + "." + name.Text + Tr.T(" não entra no arquivo: ") + origin.Detail);
                        continue;
                    }

                    if (origin.Origin != ValueOrigin.CustomParameter || origin.DataType?.PropertyType == null)
                    {
                        continue;
                    }

                    var scope = profile.IsTypeProperty(pset.Text, name.Text) ? ParameterScope.Type : ParameterScope.Instance;
                    requests.Add((specification.Name, specEntities, pset.Text, name.Text, origin.DataType.PropertyType, facet.DataType, scope));
                }
            }

            var customPairs = new HashSet<string>(requests.Select(r => r.Set + "\t" + r.Name), StringComparer.Ordinal);

            // standard Pset property exported in a user set that has a standard Revit parameter with the simple name
            // (e.g. a type different from the standard): the plugin parameter carries the type in the name, or the exporter would find its own
            var clashes = new HashSet<string>(requests
                .Where(r => exporter.FindStandardParameter(r.Set, r.Name, ParameterScope.Instance) != null
                            || exporter.FindStandardParameter(r.Set, r.Name, ParameterScope.Type) != null)
                .Select(r => r.Set + "." + r.Name), StringComparer.Ordinal);

            // type with the simple name: the one asked by most specifications (tie: the first)
            var dominant = new Dictionary<string, string>(StringComparer.Ordinal);
            var typesPerProperty = requests
                .GroupBy(r => r.Set + "\t" + r.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.GroupBy(r => r.Type, StringComparer.Ordinal)
                    .Select(t => (Type: t.Key, Specs: t.Select(x => x.Spec).Distinct().Count(), First: requests.IndexOf(t.First())))
                    .OrderByDescending(t => t.Specs).ThenBy(t => t.First).ToList());
            foreach (var pair in typesPerProperty.Where(p => p.Value.Count > 1))
            {
                dominant[pair.Key.Replace('\t', '.')] = pair.Value[0].Type;
            }

            if (format == MappingFormat.PerPropertySet)
            {
                // one set per name in the document: a single type per property, the one asked by most specifications
                foreach (var pair in typesPerProperty.Where(p => p.Value.Count > 1))
                {
                    string[] key = pair.Key.Split('\t');
                    var winner = pair.Value[0];
                    var losers = requests.Where(r => r.Set == key[0] && r.Name == key[1] && r.Type != winner.Type).ToList();
                    foreach (var byType in losers.GroupBy(r => r.Type))
                    {
                        warnings.Add(key[0] + "." + key[1] + Tr.T(": no Revit 2027 o conjunto tem um tipo só por propriedade; fica ")
                                     + winner.Type + " (" + winner.Specs + Tr.T(" ficha(s)); ")
                                     + string.Join(", ", byType.Select(r => r.Spec).Distinct()) + Tr.T(" pede ") + byType.Key + Tr.T(" e não vai passar."));
                    }

                    // the entities of the losing specifications also get the property, with the type that will be exported
                    // (the set applies to all): so Fill In Parameters shows the cell as a conflict instead of writing in vain
                    var model = requests.First(r => r.Set == key[0] && r.Name == key[1] && r.Type == winner.Type);
                    for (int i = 0; i < requests.Count; i++)
                    {
                        var r = requests[i];
                        if (r.Set == key[0] && r.Name == key[1] && r.Type != winner.Type)
                        {
                            requests[i] = (model.Spec, r.Entities, r.Set, r.Name, model.Type, model.IdsType, r.Scope);
                        }
                    }
                }
            }

            var byEntity = new Dictionary<string, List<MappedProperty>>(StringComparer.OrdinalIgnoreCase);
            var entities = new List<string>();
            foreach (var request in requests)
            {
                bool several = typesPerProperty[request.Set + "\t" + request.Name].Count > 1;
                bool plain = (!several || typesPerProperty[request.Set + "\t" + request.Name][0].Type == request.Type)
                             && !clashes.Contains(request.Set + "." + request.Name);
                string own = ParameterName(request.Set, request.Name, plain ? null : request.Type, request.Scope);
                Guid ownGuid = Fill.ParameterGuid.For(request.Set, request.Name, request.Type, request.Scope == ParameterScope.Type);
                foreach (string entity in request.Entities)
                {
                    // link: the exporter reads the Revit parameter by name; without a link, the plugin parameter
                    string? existing = profile.MappedParameter(request.Set, request.Name, entity);
                    string parameter = existing ?? own;
                    Guid? guid = existing == null ? ownGuid : (Guid?)null;
                    if (!byEntity.TryGetValue(entity, out var list))
                    {
                        list = new List<MappedProperty>();
                        byEntity[entity] = list;
                        entities.Add(entity);
                    }

                    if (!list.Any(p => p.PropertySet == request.Set && p.Name == request.Name && p.Scope == request.Scope && p.PropertyType == request.Type))
                    {
                        list.Add(new MappedProperty(request.Set, request.Name, request.Type, parameter, request.Scope, request.IdsType, request.Spec, guid));
                    }
                }
            }

            // one type per property and entity: the one asked by most specifications of the entity (tie: the first)
            foreach (string entity in entities)
            {
                var list = byEntity[entity];
                foreach (var contested in list.GroupBy(p => (p.PropertySet, p.Name, p.Scope)).Where(g => g.Count() > 1).ToList())
                {
                    var ranked = contested
                        .Select(p => (Property: p, Specs: requests.Where(r => r.Set == p.PropertySet && r.Name == p.Name && r.Scope == p.Scope
                                                                              && r.Type == p.PropertyType && r.Entities.Contains(entity))
                            .Select(r => r.Spec).Distinct().ToList()))
                        .OrderByDescending(x => x.Specs.Count)
                        .ThenBy(x => list.IndexOf(x.Property))
                        .ToList();
                    var winner = ranked[0];
                    foreach (var loser in ranked.Skip(1))
                    {
                        list.Remove(loser.Property);
                        warnings.Add(contested.Key.PropertySet + "." + contested.Key.Name + Tr.T(" em ") + entity
                                     + Tr.T(": o exportador do Revit escreve um tipo só por propriedade e entidade; fica ")
                                     + winner.Property.PropertyType + " (" + string.Join(", ", winner.Specs) + "); "
                                     + string.Join(", ", loser.Specs) + Tr.T(" pede ") + loser.Property.PropertyType + Tr.T(" e não vai passar."));
                    }
                }
            }

            if (format == MappingFormat.PerPropertySet)
            {
                // in 2027 the set has one line per property: a single parameter for all entities
                foreach (var split in byEntity.Values.SelectMany(l => l).GroupBy(p => (p.PropertySet, p.Name))
                             .Where(g => g.Select(p => p.Parameter).Distinct().Count() > 1))
                {
                    warnings.Add(split.Key.PropertySet + "." + split.Key.Name + Tr.T(": no Revit 2027 o conjunto tem um parâmetro só por ")
                                 + Tr.T("propriedade, e os vínculos por entidade apontam para ")
                                 + string.Join(", ", split.Select(p => p.Parameter).Distinct()) + Tr.T("; vale um vínculo geral."));
                }
            }

            return new ExportMapping(format, byEntity, entities, warnings.Distinct().ToList(), dominant, customPairs, clashes);
        }

        /// <summary>
        /// <c>Set.Property</c> (like the parameters Revit installs for the standard Psets), with the type when the property
        /// does not use the most common type (<c>BIMPR.Altura (Label)</c>) and <c>[Type]</c> for type parameters.
        /// </summary>
        public static string ParameterName(string propertySet, string property, string? typeSuffix, ParameterScope scope) =>
            propertySet + "." + property + (typeSuffix != null ? " (" + typeSuffix + ")" : "") + (scope == ParameterScope.Type ? "[Type]" : "");
    }
}
