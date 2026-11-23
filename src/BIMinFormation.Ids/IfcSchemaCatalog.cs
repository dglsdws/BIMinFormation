using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace BIMinFormation.Ids
{
    /// <summary>Nature of the value of an attribute (already resolved through the defined types).</summary>
    public enum AttributeValueKind
    {
        String,
        Integer,
        Real,
        Number,
        Boolean,
        Logical,
        Binary,
        Enumeration,
        Select,
        Entity,
        Aggregate,
        Unknown,
    }

    public sealed class IfcAttributeInfo
    {
        public string Entity { get; }
        public string Name { get; }
        public bool IsOptional { get; }
        public AttributeValueKind Kind { get; }

        /// <summary>Declared type (e.g. <c>IfcLabel</c>, <c>IfcWallTypeEnum</c>), or <c>null</c>.</summary>
        public string? DeclaredType { get; }

        public IfcAttributeInfo(string entity, string name, bool isOptional, AttributeValueKind kind, string? declaredType)
        {
            Entity = entity;
            Name = name;
            IsOptional = isOptional;
            Kind = kind;
            DeclaredType = declaredType;
        }
    }

    public sealed class IfcEntityInfo
    {
        public string Name { get; }
        public string? Supertype { get; }
        public bool IsAbstract { get; }
        public IReadOnlyList<IfcAttributeInfo> Attributes { get; }
        public IReadOnlyList<string> InverseAttributes { get; }

        /// <summary>Derived attributes of this entity (complete list, including inherited ones).</summary>
        public IReadOnlyCollection<string>? DerivedAttributes { get; }

        internal IfcEntityInfo(string name, string? supertype, bool isAbstract, IReadOnlyList<IfcAttributeInfo> attributes,
            IReadOnlyList<string> inverseAttributes, IReadOnlyCollection<string>? derivedAttributes)
        {
            Name = name;
            Supertype = supertype;
            IsAbstract = isAbstract;
            Attributes = attributes;
            InverseAttributes = inverseAttributes;
            DerivedAttributes = derivedAttributes;
        }

        public override string ToString() => Name;
    }

    /// <summary>Property of a standard Pset/Qto (buildingSMART template).</summary>
    public sealed class IfcPropertyTemplate
    {
        public string PropertySet { get; }
        public string Name { get; }

        /// <summary>E.g. <c>P_SINGLEVALUE</c>, <c>P_ENUMERATEDVALUE</c>, <c>Q_LENGTH</c>, <c>COMPLEX</c>.</summary>
        public string TemplateType { get; }

        /// <summary>
        /// IFC type in upper case, as in the IDS <c>dataType</c> (e.g. <c>IFCBOOLEAN</c>). For quantities, the matching measure
        /// (<c>Q_LENGTH</c> → <c>IFCLENGTHMEASURE</c>). <c>null</c> when the template does not define it (references, complex).
        /// </summary>
        public string? DataType { get; }

        public IReadOnlyList<string> EnumerationValues { get; }

        internal IfcPropertyTemplate(string propertySet, string name, string templateType, string? dataType,
            IReadOnlyList<string> enumerationValues)
        {
            PropertySet = propertySet;
            Name = name;
            TemplateType = templateType;
            DataType = dataType;
            EnumerationValues = enumerationValues;
        }
    }

    public sealed class IfcPropertySetTemplate
    {
        public string Name { get; }
        public bool IsQuantitySet { get; }
        public string TemplateType { get; }

        /// <summary>Applicable entities as in the template (e.g. <c>IfcWall</c>, <c>IfcActuator/ELECTRICACTUATOR</c>).</summary>
        public IReadOnlyList<string> ApplicableEntities { get; }

        public IReadOnlyList<IfcPropertyTemplate> Properties { get; }

        internal IfcPropertySetTemplate(string name, bool isQuantitySet, string templateType,
            IReadOnlyList<string> applicableEntities, IReadOnlyList<IfcPropertyTemplate> properties)
        {
            Name = name;
            IsQuantitySet = isQuantitySet;
            TemplateType = templateType;
            ApplicableEntities = applicableEntities;
            Properties = properties;
        }

        public IfcPropertyTemplate? Property(string name) =>
            Properties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
    }

    /// <summary>How an attribute name resolves on an entity.</summary>
    public enum AttributeLookup
    {
        Direct,
        Derived,
        Inverse,
        Missing,
    }

    /// <summary>
    /// Catalog of an IFC schema (entities, attributes, enumerations, defined types and standard Psets/Qtos), generated
    /// from IfcOpenShell by <c>tools/oracle/dump_schema.py</c> and embedded in the DLL. Names are looked up ignoring case
    /// (the IDS uses <c>IFCWALL</c>, the schema <c>IfcWall</c>).
    /// </summary>
    public sealed class IfcSchemaCatalog
    {
        private static readonly ConcurrentDictionary<IfcSchemaVersion, IfcSchemaCatalog> Cache =
            new ConcurrentDictionary<IfcSchemaVersion, IfcSchemaCatalog>();

        private static readonly Dictionary<string, string> QuantityTypes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Q_LENGTH"] = "IFCLENGTHMEASURE",
            ["Q_AREA"] = "IFCAREAMEASURE",
            ["Q_VOLUME"] = "IFCVOLUMEMEASURE",
            ["Q_COUNT"] = "IFCCOUNTMEASURE",
            ["Q_WEIGHT"] = "IFCMASSMEASURE",
            ["Q_TIME"] = "IFCTIMEMEASURE",
            ["Q_NUMBER"] = "IFCNUMERICMEASURE",
        };

        private readonly Dictionary<string, IfcEntityInfo> _entities = new Dictionary<string, IfcEntityInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AttributeValueKind> _types = new Dictionary<string, AttributeValueKind>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _typeNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IReadOnlyList<string>> _enumerations = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IfcPropertySetTemplate> _psets = new Dictionary<string, IfcPropertySetTemplate>(StringComparer.Ordinal);

        public IfcSchemaVersion Version { get; }
        public string Name { get; private set; } = "";

        private IfcSchemaCatalog(IfcSchemaVersion version)
        {
            Version = version;
        }

        public static IfcSchemaCatalog For(IfcSchemaVersion version) => Cache.GetOrAdd(version, Load);

        public static string ResourceName(IfcSchemaVersion version) =>
            "BIMinFormation.Ids.Schema.ifc-" + version switch
            {
                IfcSchemaVersion.Ifc2X3 => "IFC2X3",
                IfcSchemaVersion.Ifc4 => "IFC4",
                _ => "IFC4X3_ADD2",
            } + ".txt";

        public IEnumerable<IfcEntityInfo> Entities => _entities.Values;

        public IEnumerable<IfcPropertySetTemplate> PropertySets => _psets.Values;

        public IfcEntityInfo? Entity(string name) => _entities.TryGetValue(name, out var entity) ? entity : null;

        public bool HasEntity(string name) => _entities.ContainsKey(name);

        /// <summary>The entity and its supertypes, from the most specific to the most general.</summary>
        public IEnumerable<IfcEntityInfo> Ancestry(string name)
        {
            var current = Entity(name);
            for (int depth = 0; current != null && depth < 64; depth++)
            {
                yield return current;
                current = current.Supertype == null ? null : Entity(current.Supertype);
            }
        }

        public bool IsSubtypeOf(string entity, string ancestor) =>
            Ancestry(entity).Any(e => string.Equals(e.Name, ancestor, StringComparison.OrdinalIgnoreCase));

        /// <summary>Direct attribute (own or inherited), or <c>null</c>.</summary>
        public IfcAttributeInfo? Attribute(string entity, string attribute)
        {
            foreach (var info in Ancestry(entity))
            {
                var found = info.Attributes.FirstOrDefault(a => a.Name == attribute);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        /// <summary>All direct attributes (inherited first, in STEP order).</summary>
        public IReadOnlyList<IfcAttributeInfo> AllAttributes(string entity) =>
            Ancestry(entity).Reverse().SelectMany(e => e.Attributes).ToList();

        public AttributeLookup LookupAttribute(string entity, string attribute)
        {
            var ancestry = Ancestry(entity).ToList();
            var derived = ancestry.Select(e => e.DerivedAttributes).FirstOrDefault(d => d != null);
            if (derived != null && derived.Contains(attribute))
            {
                return AttributeLookup.Derived;
            }

            if (Attribute(entity, attribute) != null)
            {
                return AttributeLookup.Direct;
            }

            return ancestry.Any(e => e.InverseAttributes.Contains(attribute)) ? AttributeLookup.Inverse : AttributeLookup.Missing;
        }

        /// <summary>
        /// Values of the entity's <c>PredefinedType</c> enumeration; <c>null</c> if the entity has no such attribute.
        /// </summary>
        public IReadOnlyList<string>? PredefinedTypes(string entity)
        {
            var attribute = Attribute(entity, "PredefinedType");
            return attribute?.DeclaredType != null && _enumerations.TryGetValue(attribute.DeclaredType, out var values)
                ? values
                : null;
        }

        /// <summary>
        /// The entity accepts a predefined type: it has <c>PredefinedType</c>, or the matching type (<c>…Type</c>/<c>…Style</c>)
        /// has it, or it is a type with <c>ElementType</c>/<c>ProcessType</c>.
        /// </summary>
        public bool SupportsPredefinedType(string entity)
        {
            if (Attribute(entity, "PredefinedType") != null || Attribute(entity, "ElementType") != null
                || Attribute(entity, "ProcessType") != null)
            {
                return true;
            }

            return (HasEntity(entity + "Type") && Attribute(entity + "Type", "PredefinedType") != null)
                   || (HasEntity(entity + "Style") && Attribute(entity + "Style", "PredefinedType") != null);
        }

        public IReadOnlyList<string>? Enumeration(string name) =>
            _enumerations.TryGetValue(name, out var values) ? values : null;

        /// <summary>Nature of a defined type (e.g. <c>IFCLENGTHMEASURE</c> → Real); <c>null</c> if it does not exist.</summary>
        public AttributeValueKind? TypeKind(string definedType) =>
            _types.TryGetValue(definedType, out var kind) ? kind : (AttributeValueKind?)null;

        /// <summary>Name of a defined type as spelled in the schema (e.g. <c>IFCLENGTHMEASURE</c> → <c>IfcLengthMeasure</c>).</summary>
        public string? TypeName(string definedType) =>
            _typeNames.TryGetValue(definedType, out var name) ? name : null;

        public IfcPropertySetTemplate? PropertySet(string name) => _psets.TryGetValue(name, out var pset) ? pset : null;

        private static IfcSchemaCatalog Load(IfcSchemaVersion version)
        {
            var assembly = typeof(IfcSchemaCatalog).GetTypeInfo().Assembly;
            using var stream = assembly.GetManifestResourceStream(ResourceName(version))
                ?? throw new InvalidOperationException("Schema catalog not embedded: " + ResourceName(version));
            using var reader = new StreamReader(stream);
            var catalog = new IfcSchemaCatalog(version);
            catalog.Parse(reader);
            return catalog;
        }

        private void Parse(TextReader reader)
        {
            var entities = new List<(string Name, string? Supertype, bool Abstract)>();
            var attributes = new Dictionary<string, List<IfcAttributeInfo>>(StringComparer.OrdinalIgnoreCase);
            var inverse = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            var derived = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var psets = new List<(string Name, bool Qto, string Template, string[] Applicable)>();
            var props = new Dictionary<string, List<IfcPropertyTemplate>>(StringComparer.Ordinal);

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                string[] f = line.Split('\t');
                switch (f[0])
                {
                    case "SCHEMA":
                        Name = f[1];
                        break;
                    case "TYPE":
                        _types[f[1]] = ParseKind(f[2]);
                        _typeNames[f[1]] = f[1];
                        break;
                    case "ENUM":
                        _enumerations[f[1]] = f[2].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        break;
                    case "ENTITY":
                        entities.Add((f[1], f[2] == "-" ? null : f[2], f[3] == "1"));
                        break;
                    case "ATTR":
                        if (!attributes.TryGetValue(f[1], out var list))
                        {
                            list = new List<IfcAttributeInfo>();
                            attributes[f[1]] = list;
                        }

                        list.Add(new IfcAttributeInfo(f[1], f[2], f[3] == "1", ParseKind(f[4]), f[5] == "-" ? null : f[5]));
                        break;
                    case "INVERSE":
                        inverse[f[1]] = f[2].Split(',');
                        break;
                    case "DERIVED":
                        derived[f[1]] = new HashSet<string>(f[2].Split(','), StringComparer.Ordinal);
                        break;
                    case "PSET":
                        psets.Add((f[1], f[2] == "Q", f[3], f[4] == "-" ? new string[0] : f[4].Split(',')));
                        break;
                    case "PROP":
                        if (!props.TryGetValue(f[1], out var propList))
                        {
                            propList = new List<IfcPropertyTemplate>();
                            props[f[1]] = propList;
                        }

                        string? dataType = f[4] != "-" ? f[4].ToUpperInvariant()
                            : QuantityTypes.TryGetValue(f[3], out var quantityType) ? quantityType : null;
                        var values = f.Length > 5 && f[5].Length > 0 ? f[5].Split(',') : new string[0];
                        propList.Add(new IfcPropertyTemplate(f[1], f[2], f[3], dataType, values));
                        break;
                }
            }

            foreach (var (name, supertype, isAbstract) in entities)
            {
                _entities[name] = new IfcEntityInfo(name, supertype, isAbstract,
                    attributes.TryGetValue(name, out var own) ? own : (IReadOnlyList<IfcAttributeInfo>)new IfcAttributeInfo[0],
                    inverse.TryGetValue(name, out var inv) ? inv : new string[0],
                    derived.TryGetValue(name, out var der) ? der : null);
            }

            // the same Pset may appear more than once in the templates (e.g. occurrence and type): merge them
            foreach (var group in psets.GroupBy(p => p.Name, StringComparer.Ordinal))
            {
                var first = group.First();
                _psets[group.Key] = new IfcPropertySetTemplate(group.Key, first.Qto, first.Template,
                    group.SelectMany(p => p.Applicable).Distinct(StringComparer.Ordinal).ToList(),
                    props.TryGetValue(group.Key, out var list)
                        ? list.GroupBy(p => p.Name, StringComparer.Ordinal).Select(g => g.First()).ToList()
                        : new List<IfcPropertyTemplate>());
            }
        }

        private static AttributeValueKind ParseKind(string text)
        {
            switch (text)
            {
                case "string": return AttributeValueKind.String;
                case "integer": return AttributeValueKind.Integer;
                case "real": return AttributeValueKind.Real;
                case "number": return AttributeValueKind.Number;
                case "boolean": return AttributeValueKind.Boolean;
                case "logical": return AttributeValueKind.Logical;
                case "binary": return AttributeValueKind.Binary;
                case "enum": return AttributeValueKind.Enumeration;
                case "select": return AttributeValueKind.Select;
                case "entity": return AttributeValueKind.Entity;
                case "aggregate": return AttributeValueKind.Aggregate;
                default: return AttributeValueKind.Unknown;
            }
        }
    }
}
