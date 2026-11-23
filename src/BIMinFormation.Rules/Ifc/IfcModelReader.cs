using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BIMinFormation.Ids;

namespace BIMinFormation.Rules.Ifc
{
    /// <summary>
    /// Reads an IFC (STEP) file and builds the <see cref="ModelState"/> the rules engine evaluates. Reproduces
    /// <c>tools/oracle/dump_state.py</c> (IfcOpenShell): effective predefined type, properties inherited from the type,
    /// classifications, material, relationships and units converted to SI.
    /// </summary>
    public sealed class IfcModelReader
    {
        private static readonly string[] RootAttributes = { "GlobalId", "OwnerHistory", "Name", "Description" };

        private static readonly (string Name, double Factor)[] Prefixes =
        {
            ("EXA", 1e18), ("PETA", 1e15), ("TERA", 1e12), ("GIGA", 1e9), ("MEGA", 1e6), ("KILO", 1e3),
            ("HECTO", 1e2), ("DECA", 1e1), ("DECI", 1e-1), ("CENTI", 1e-2), ("MILLI", 1e-3), ("MICRO", 1e-6),
            ("NANO", 1e-9), ("PICO", 1e-12), ("FEMTO", 1e-15), ("ATTO", 1e-18),
        };

        // ifcopenshell.util.unit.si_conversions (only the lower-case keys match name.lower())
        private static readonly Dictionary<string, double> SiConversions = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["thou"] = 2.54e-05, ["inch"] = 0.0254, ["foot"] = 0.3048, ["yard"] = 0.914, ["mile"] = 1609,
            ["square thou"] = 6.4516e-10, ["square inch"] = 0.0006452, ["square foot"] = 0.09290304,
            ["square yard"] = 0.83612736, ["acre"] = 4046.86, ["square mile"] = 2588881, ["cubic thou"] = 1.6387064e-14,
            ["cubic inch"] = 1.639e-05, ["cubic foot"] = 0.02831684671168849, ["cubic yard"] = 0.7636,
            ["cubic mile"] = 4165509529, ["litre"] = 0.001, ["pint UK"] = 0.000568, ["pint US"] = 0.000473,
            ["gallon UK"] = 0.004546, ["gallon US"] = 0.003785, ["degree"] = 0.017453292519943295, ["ounce"] = 0.02835,
            ["pound"] = 0.454, ["ton UK"] = 1016.0469088, ["ton US"] = 907.18474, ["tonne"] = 1000.0,
            ["lbf"] = 4.4482216153, ["kip"] = 4448.2216153, ["psi"] = 6894.7572932, ["ksi"] = 6894757.2932,
            ["minute"] = 60, ["hour"] = 3600, ["day"] = 86400, ["btu"] = 1055.056, ["fahrenheit"] = 1.8,
        };

        private static readonly HashSet<string> SiTypeNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "ABSORBEDDOSEUNIT", "AMOUNTOFSUBSTANCEUNIT", "AREAUNIT", "DOSEEQUIVALENTUNIT", "ELECTRICCAPACITANCEUNIT",
            "ELECTRICCHARGEUNIT", "ELECTRICCONDUCTANCEUNIT", "ELECTRICCURRENTUNIT", "ELECTRICRESISTANCEUNIT",
            "ELECTRICVOLTAGEUNIT", "ENERGYUNIT", "FORCEUNIT", "FREQUENCYUNIT", "ILLUMINANCEUNIT", "INDUCTANCEUNIT",
            "LENGTHUNIT", "LUMINOUSFLUXUNIT", "LUMINOUSINTENSITYUNIT", "MAGNETICFLUXDENSITYUNIT", "MAGNETICFLUXUNIT",
            "MASSUNIT", "PLANEANGLEUNIT", "POWERUNIT", "PRESSUREUNIT", "RADIOACTIVITYUNIT", "SOLIDANGLEUNIT",
            "THERMODYNAMICTEMPERATUREUNIT", "TIMEUNIT", "VOLUMEUNIT", "USERDEFINED",
        };

        private readonly StepFile _file;
        private readonly IfcSchemaCatalog _catalog;
        private readonly bool _is2X3;
        private readonly Dictionary<string, HashSet<string>> _ancestry = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, int>> _attributeIndex = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _inverseNames = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly Dictionary<(long, string), List<long>> _inverse = new Dictionary<(long, string), List<long>>();
        private readonly Dictionary<string, long?> _projectUnits = new Dictionary<string, long?>(StringComparer.Ordinal);
        private List<long>? _extendedMaterialProperties;
        private List<long>? _units;
        private readonly Dictionary<string, bool> _relevant = new Dictionary<string, bool>(StringComparer.Ordinal);
        private bool? _hasProject;

        public IfcSchemaVersion Schema { get; }

        private IfcModelReader(StepFile file)
        {
            _file = file;
            Schema = SchemaOf(file.Schema);
            _is2X3 = Schema == IfcSchemaVersion.Ifc2X3;
            _catalog = IfcSchemaCatalog.For(Schema);
            BuildInverses();
        }

        /// <summary>
        /// Reads the IFC. With <paramref name="allInstances"/>, returns every instance (like the oracle); without it, only the
        /// object definitions (elements, types, spaces, groups...), materials and profiles, and the instances of the entities
        /// <paramref name="includeEntity"/> accepts (upper-case name, e.g. <c>IFCPERSON</c>).
        /// </summary>
        public static ModelState Read(string path, bool allInstances = false, Func<string, bool>? includeEntity = null) =>
            Read(StepFile.Open(path), allInstances, includeEntity);

        public static ModelState Read(StepFile file, bool allInstances = false, Func<string, bool>? includeEntity = null)
        {
            var reader = new IfcModelReader(file);
            var elements = new List<ElementState>();
            foreach (long id in file.Ids)
            {
                if (id <= 0) continue;
                if (!allInstances && !reader.IsRelevant(id) && (includeEntity == null || !includeEntity(file.TypeOf(id)!))) continue;
                elements.Add(reader.Element(id));
            }

            file.ClearCache();
            return new ModelState(reader.Schema, elements);
        }

        public static IfcSchemaVersion SchemaOf(string header)
        {
            string schema = header.Trim().ToUpperInvariant();
            if (schema == "IFC2X3") return IfcSchemaVersion.Ifc2X3;
            if (schema == "IFC4") return IfcSchemaVersion.Ifc4;
            if (schema.StartsWith("IFC4X3", StringComparison.Ordinal)) return IfcSchemaVersion.Ifc4X3Add2;
            throw new InvalidDataException(Tr.T("Esquema IFC não suportado: ") + header);
        }

        private bool IsRelevant(long id)
        {
            string type = _file.TypeOf(id)!;
            if (_relevant.TryGetValue(type, out bool relevant)) return relevant;
            relevant = IsA(id, "IfcObjectDefinition") || IsA(id, "IfcMaterialDefinition") || IsA(id, "IfcProfileDef") || IsA(id, "IfcMaterial");
            _relevant[type] = relevant;
            return relevant;
        }

        // ---- schema
        private HashSet<string> Ancestry(string type)
        {
            if (_ancestry.TryGetValue(type, out var set)) return set;
            set = new HashSet<string>(_catalog.Ancestry(type).Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
            _ancestry[type] = set;
            return set;
        }

        private bool IsA(long id, string entity)
        {
            string? type = _file.TypeOf(id);
            return type != null && Ancestry(type).Contains(entity);
        }

        private bool HasInverse(long id, string inverse)
        {
            string? type = _file.TypeOf(id);
            if (type == null) return false;
            if (!_inverseNames.TryGetValue(type, out var names))
            {
                names = new HashSet<string>(_catalog.Ancestry(type).SelectMany(e => e.InverseAttributes), StringComparer.Ordinal);
                _inverseNames[type] = names;
            }

            return names.Contains(inverse);
        }

        private readonly Dictionary<string, IReadOnlyList<IfcAttributeInfo>> _allAttributes =
            new Dictionary<string, IReadOnlyList<IfcAttributeInfo>>(StringComparer.Ordinal);

        private readonly Dictionary<string, string> _measureUnitTypes = new Dictionary<string, string>(StringComparer.Ordinal);

        private IReadOnlyList<IfcAttributeInfo> AllAttributes(string type)
        {
            if (!_allAttributes.TryGetValue(type, out var all))
            {
                all = _catalog.AllAttributes(type);
                _allAttributes[type] = all;
            }

            return all;
        }

        private int IndexOf(string type, string attribute)
        {
            if (!_attributeIndex.TryGetValue(type, out var map))
            {
                map = new Dictionary<string, int>(StringComparer.Ordinal);
                var all = AllAttributes(type);
                for (int i = 0; i < all.Count; i++)
                {
                    if (!map.ContainsKey(all[i].Name)) map[all[i].Name] = i;
                }

                _attributeIndex[type] = map;
            }

            return map.TryGetValue(attribute, out int index) ? index : -1;
        }

        /// <summary>Value of the direct attribute (<c>null</c> if the entity does not have the attribute).</summary>
        private StepValue? Get(long id, string attribute)
        {
            string? type = _file.TypeOf(id);
            if (type == null) return null;
            int index = IndexOf(type, attribute);
            if (index < 0) return null;
            var arguments = _file.Arguments(id);
            return index < arguments.Count ? arguments[index] : StepValue.Null;
        }

        private long? Ref(long id, string attribute)
        {
            var value = Get(id, attribute);
            return value != null && value.Kind == StepKind.Reference && _file.Contains(value.Reference) ? value.Reference : (long?)null;
        }

        private IEnumerable<long> Refs(long id, string attribute)
        {
            var value = Get(id, attribute);
            if (value == null || value.Kind != StepKind.List) yield break;
            foreach (var item in value.Items)
            {
                if (item.Kind == StepKind.Reference && _file.Contains(item.Reference)) yield return item.Reference;
            }
        }

        /// <summary>getattr(inst, name, None) for text/enumeration.</summary>
        private string? Text(long id, string attribute, out bool exists)
        {
            var value = Get(id, attribute);
            exists = value != null;
            return value != null && (value.Kind == StepKind.String || value.Kind == StepKind.Enumeration) ? value.Text : null;
        }

        private string? Text(long id, string attribute) => Text(id, attribute, out _);

        private IReadOnlyList<long> Inverse(long id, string inverse)
        {
            if (!HasInverse(id, inverse)) return Array.Empty<long>();
            return _inverse.TryGetValue((id, inverse), out var list) ? (IReadOnlyList<long>)list : Array.Empty<long>();
        }

        // ---- inverse relationship index
        private void BuildInverses()
        {
            foreach (long rel in _file.Ids)
            {
                string type = _file.TypeOf(rel)!;
                if (!type.StartsWith("IFCREL", StringComparison.Ordinal) && !type.Contains("PROPERTIES")) continue;
                if (IsA(rel, "IfcRelContainedInSpatialStructure"))
                {
                    AddInverse(rel, Refs(rel, "RelatedElements"), "ContainedInStructure");
                }
                else if (IsA(rel, "IfcRelAggregates"))
                {
                    AddInverse(rel, Refs(rel, "RelatedObjects"), "Decomposes");
                }
                else if (IsA(rel, "IfcRelNests"))
                {
                    AddInverse(rel, Refs(rel, "RelatedObjects"), _is2X3 ? "Decomposes" : "Nests");
                }
                else if (IsA(rel, "IfcRelAssigns"))
                {
                    AddInverse(rel, Refs(rel, "RelatedObjects"), "HasAssignments");
                }
                else if (IsA(rel, "IfcRelFillsElement"))
                {
                    AddInverse(rel, Single(Ref(rel, "RelatedBuildingElement")), "FillsVoids");
                }
                else if (IsA(rel, "IfcRelVoidsElement"))
                {
                    AddInverse(rel, Single(Ref(rel, "RelatedOpeningElement")), "VoidsElements");
                }
                else if (IsA(rel, "IfcRelDefinesByProperties"))
                {
                    AddInverse(rel, Refs(rel, "RelatedObjects"), "IsDefinedBy");
                }
                else if (IsA(rel, "IfcRelDefinesByType"))
                {
                    AddInverse(rel, Refs(rel, "RelatedObjects"), _is2X3 ? "IsDefinedBy" : "IsTypedBy");
                }
                else if (IsA(rel, "IfcRelAssociates"))
                {
                    AddInverse(rel, Refs(rel, "RelatedObjects"), "HasAssociations");
                }
                else if (!_is2X3 && IsA(rel, "IfcMaterialProperties"))
                {
                    AddInverse(rel, Single(Ref(rel, "Material")), "HasProperties");
                }
                else if (!_is2X3 && IsA(rel, "IfcProfileProperties"))
                {
                    AddInverse(rel, Single(Ref(rel, "ProfileDefinition")), "HasProperties");
                }
            }
        }

        private static IEnumerable<long> Single(long? id)
        {
            if (id.HasValue) yield return id.Value;
        }

        private void AddInverse(long rel, IEnumerable<long> targets, string inverse)
        {
            foreach (long target in targets)
            {
                if (!_inverse.TryGetValue((target, inverse), out var list))
                {
                    list = new List<long>();
                    _inverse[(target, inverse)] = list;
                }

                if (!list.Contains(rel)) list.Add(rel);
            }
        }

        // ---- element
        private ElementState Element(long id)
        {
            string entity = _file.TypeOf(id)!;
            var attributes = Attributes(id);
            bool isObjectDefinition = IsA(id, "IfcObjectDefinition");
            string? typeEntity = null;
            string? predefined = null;
            bool userDefined = false;
            if (isObjectDefinition)
            {
                long? type = TypeOf(id);
                if (type.HasValue && type.Value != id) typeEntity = _file.TypeOf(type.Value);
                predefined = PredefinedType(id);
                userDefined = IsUserDefined(id);
            }

            IReadOnlyList<PropertySetState>? psets = null;
            if (isObjectDefinition || IsA(id, "IfcMaterialDefinition") || IsA(id, "IfcProfileDef") || IsA(id, "IfcMaterial"))
            {
                psets = PropertySets(id);
            }

            IReadOnlyList<ClassificationState>? classifications = null;
            MaterialState? material = null;
            ElementRelations? relations = null;
            if (isObjectDefinition)
            {
                classifications = Classifications(id);
                material = Material(id);
                relations = Relations(id);
            }

            return new ElementState(id.ToString(CultureInfo.InvariantCulture), entity, typeEntity, predefined, userDefined,
                attributes, psets, classifications, material, relations);
        }

        private Dictionary<string, StateValue> Attributes(long id)
        {
            string type = _file.TypeOf(id)!;
            var result = new Dictionary<string, StateValue>(StringComparer.Ordinal);
            var schema = AllAttributes(type);
            var arguments = _file.Arguments(id);
            for (int i = 0; i < schema.Count; i++)
            {
                var value = i < arguments.Count ? arguments[i] : StepValue.Null;
                result[schema[i].Name] = Typed(value, schema[i].Kind);
            }

            return result;
        }

        /// <summary>typed(): attribute value as IfcOpenShell returns it.</summary>
        private static StateValue Typed(StepValue value, AttributeValueKind kind, bool logical = true)
        {
            switch (value.Kind)
            {
                case StepKind.Null:
                case StepKind.Derived:
                    return StateValue.Null;
                case StepKind.Reference:
                case StepKind.Typed:
                    return StateValue.EntityReference;
                case StepKind.List:
                    return StateValue.Aggregate(value.Items.Count);
                case StepKind.Integer:
                    return kind == AttributeValueKind.Real || kind == AttributeValueKind.Number
                        ? StateValue.Of((double)value.Integer)
                        : StateValue.Of(value.Integer);
                case StepKind.Real:
                    return StateValue.Of(value.Real);
                case StepKind.Enumeration:
                    if (kind == AttributeValueKind.Boolean || kind == AttributeValueKind.Logical)
                    {
                        string text = value.Text!.ToUpperInvariant();
                        if (text == "T") return StateValue.Of(true);
                        if (text == "F") return StateValue.Of(false);
                        if (kind == AttributeValueKind.Logical) return logical ? StateValue.Unknown : StateValue.Of("UNKNOWN");
                    }

                    return StateValue.Of(value.Text!);
                case StepKind.Binary:
                    return StateValue.Of(Bits(value.Text!));
                default:
                    return StateValue.Of(value.Text ?? "");
            }
        }

        /// <summary>Value of a defined type (e.g. <c>IFCLENGTHMEASURE(2.5)</c>), like wrapped().</summary>
        private StateValue Wrapped(StepValue value)
        {
            if (value.Kind != StepKind.Typed) return Typed(value, AttributeValueKind.Unknown);
            var kind = _catalog.TypeKind(value.TypeName!) ?? AttributeValueKind.Unknown;
            return Typed(value.Inner!, kind);
        }

        private static string Bits(string hex)
        {
            if (hex.Length == 0) return "";
            int unused = hex[0] - '0';
            var bits = new StringBuilder();
            for (int i = 1; i < hex.Length; i++)
            {
                int digit = System.Convert.ToInt32(hex[i].ToString(), 16);
                bits.Append(System.Convert.ToString(digit, 2).PadLeft(4, '0'));
            }

            return unused > 0 && unused <= bits.Length ? bits.ToString(unused, bits.Length - unused) : bits.ToString();
        }

        // ---- type and predefined type (ifcopenshell.util.element)
        private long? TypeOf(long id)
        {
            if (IsA(id, "IfcTypeObject")) return id;
            if (!_is2X3)
            {
                var typedBy = Inverse(id, "IsTypedBy");
                return typedBy.Count > 0 ? Ref(typedBy[0], "RelatingType") : null;
            }

            foreach (long rel in Inverse(id, "IsDefinedBy"))
            {
                if (IsA(rel, "IfcRelDefinesByType")) return Ref(rel, "RelatingType");
            }

            return null;
        }

        private string? PredefinedType(long id)
        {
            long? type = TypeOf(id);
            if (type.HasValue)
            {
                string? value = Text(type.Value, "PredefinedType");
                if (value == "USERDEFINED" || string.IsNullOrEmpty(value))
                {
                    value = Text(type.Value, "ElementType", out bool exists);
                    if (!exists) value = Text(type.Value, "ProcessType");
                }

                if (!string.IsNullOrEmpty(value) && value != "NOTDEFINED") return value;
            }

            string? own = Text(id, "PredefinedType");
            if (own == "USERDEFINED" || string.IsNullOrEmpty(own)) own = Text(id, "ObjectType");
            return own;
        }

        private bool IsUserDefined(long id)
        {
            long? type = TypeOf(id);
            if (type.HasValue)
            {
                string? value = Text(type.Value, "PredefinedType");
                if (value == "USERDEFINED") return true;
                if (string.IsNullOrEmpty(value))
                {
                    value = Text(type.Value, "ElementType", out bool exists);
                    if (!exists) value = Text(type.Value, "ProcessType");
                    if (!string.IsNullOrEmpty(value)) return true;
                }

                if (!string.IsNullOrEmpty(value) && value != "NOTDEFINED") return false;
            }

            string? own = Text(id, "PredefinedType");
            if (own == "USERDEFINED") return true;
            if (string.IsNullOrEmpty(own)) return !string.IsNullOrEmpty(Text(id, "ObjectType"));
            return false;
        }

        // ---- properties
        private IReadOnlyList<PropertySetState> PropertySets(long id)
        {
            var order = new List<string>();
            var merged = new Dictionary<string, (List<string> Order, Dictionary<string, PropertyState> Props)>(StringComparer.Ordinal);

            void Add(IEnumerable<long> definitions, PropertySource source)
            {
                foreach (long definition in definitions)
                {
                    string name = Text(definition, "Name")!;
                    if (!merged.TryGetValue(name, out var set))
                    {
                        set = (new List<string>(), new Dictionary<string, PropertyState>(StringComparer.Ordinal));
                        merged[name] = set;
                        order.Add(name);
                    }

                    foreach (var property in DefinitionProperties(definition, source))
                    {
                        if (!set.Props.ContainsKey(property.Name)) set.Order.Add(property.Name);
                        set.Props[property.Name] = property;
                    }
                }
            }

            if (IsA(id, "IfcObject"))
            {
                long? type = TypeOf(id);
                if (type.HasValue && type.Value != id) Add(OwnDefinitions(type.Value), PropertySource.Type);
            }

            Add(OwnDefinitions(id), PropertySource.Occurrence);
            return order.Select(name => new PropertySetState(name,
                merged[name].Order.Select(p => merged[name].Props[p]).ToList())).ToList();
        }

        private List<long> OwnDefinitions(long id)
        {
            var result = new List<long>();
            if (IsA(id, "IfcTypeObject"))
            {
                result.AddRange(Refs(id, "HasPropertySets"));
            }
            else if (IsA(id, "IfcMaterialDefinition") || IsA(id, "IfcProfileDef") || (_is2X3 && IsA(id, "IfcMaterial")))
            {
                if (_is2X3)
                {
                    if (IsA(id, "IfcMaterial"))
                    {
                        _extendedMaterialProperties ??= _file.Ids.Where(i => IsA(i, "IfcExtendedMaterialProperties")).ToList();
                        result.AddRange(_extendedMaterialProperties.Where(d => Ref(d, "Material") == id));
                    }
                }
                else
                {
                    result.AddRange(Inverse(id, "HasProperties"));
                }
            }
            else
            {
                foreach (long rel in Inverse(id, "IsDefinedBy"))
                {
                    if (!IsA(rel, "IfcRelDefinesByProperties")) continue;
                    var definition = Get(rel, "RelatingPropertyDefinition");
                    if (definition == null) continue;
                    if (definition.Kind == StepKind.Reference)
                    {
                        if (_file.Contains(definition.Reference)) result.Add(definition.Reference);
                    }
                    else if (definition.Kind == StepKind.Typed && definition.Inner!.Kind == StepKind.List)
                    {
                        // IfcPropertySetDefinitionSet
                        result.AddRange(definition.Inner.Items.Where(v => v.Kind == StepKind.Reference && _file.Contains(v.Reference))
                            .Select(v => v.Reference));
                    }
                }
            }

            return result.Where(d => !string.IsNullOrEmpty(Text(d, "Name"))).ToList();
        }

        private IEnumerable<PropertyState> DefinitionProperties(long definition, PropertySource source)
        {
            IEnumerable<long> properties;
            if (IsA(definition, "IfcPropertySet")) properties = Refs(definition, "HasProperties");
            else if (IsA(definition, "IfcElementQuantity")) properties = Refs(definition, "Quantities");
            else if (_catalog.HasEntity("IfcPreDefinedPropertySet") && IsA(definition, "IfcPreDefinedPropertySet"))
                return PredefinedProperties(definition, source);
            else if (IsA(definition, "IfcMaterialProperties") || IsA(definition, "IfcProfileProperties"))
                properties = Refs(definition, "Properties");
            else return Enumerable.Empty<PropertyState>();
            return properties.Select(p => Property(p, source)).ToList();
        }

        private IEnumerable<PropertyState> PredefinedProperties(long definition, PropertySource source)
        {
            var result = new List<PropertyState>();
            string type = _file.TypeOf(definition)!;
            var schema = AllAttributes(type);
            var arguments = _file.Arguments(definition);
            for (int i = 0; i < schema.Count; i++)
            {
                if (Array.IndexOf(RootAttributes, schema[i].Name) >= 0) continue;
                var value = i < arguments.Count ? arguments[i] : StepValue.Null;
                if (value.IsNull || value.Kind == StepKind.Reference || value.Kind == StepKind.Typed) continue;
                result.Add(new PropertyState(schema[i].Name, PropertyKind.Predefined, null,
                    new[] { Typed(value, schema[i].Kind, logical: false) }, source));
            }

            return result;
        }

        private PropertyState Property(long id, PropertySource source)
        {
            string name = Text(id, "Name")!;
            string? dataType = null;
            var values = new List<StateValue>();
            List<TableColumn>? columns = null;
            PropertyKind kind;
            if (IsA(id, "IfcPropertySingleValue"))
            {
                kind = PropertyKind.Single;
                var nominal = Get(id, "NominalValue");
                if (nominal != null && !nominal.IsNull)
                {
                    dataType = TypeName(nominal);
                    long? unit = PropertyUnit(id);
                    var value = Wrapped(nominal);
                    if (IsNumber(value) && unit.HasValue) value = StateValue.Of(Convert(value.Number, unit.Value, massInKilo: true));
                    values.Add(value);
                }
            }
            else if (IsA(id, "IfcPhysicalSimpleQuantity"))
            {
                kind = PropertyKind.Quantity;
                var attribute = AllAttributes(_file.TypeOf(id)!)[3];
                dataType = attribute.DeclaredType?.ToUpperInvariant();
                var arguments = _file.Arguments(id);
                var raw = arguments.Count > 3 ? arguments[3] : StepValue.Null;
                if (!raw.IsNull)
                {
                    long? unit = PropertyUnit(id);
                    var value = Typed(raw, attribute.Kind);
                    if (unit.HasValue && HasName(unit.Value) && IsNumber(value)) values.Add(StateValue.Of(Convert(value.Number, unit.Value)));
                    else values.Add(value);
                }
            }
            else if (IsA(id, "IfcPropertyEnumeratedValue"))
            {
                kind = PropertyKind.Enumerated;
                var items = Items(id, "EnumerationValues");
                if (items.Count > 0) dataType = TypeName(items[0]);
                values.AddRange(items.Select(Wrapped));
            }
            else if (IsA(id, "IfcPropertyListValue"))
            {
                kind = PropertyKind.List;
                var items = Items(id, "ListValues");
                if (items.Count > 0) dataType = TypeName(items[0]);
                long? unit = PropertyUnit(id);
                foreach (var item in items)
                {
                    var value = Wrapped(item);
                    if (IsNumber(value) && unit.HasValue && HasName(unit.Value)) value = StateValue.Of(Convert(value.Number, unit.Value));
                    values.Add(value);
                }
            }
            else if (IsA(id, "IfcPropertyBoundedValue"))
            {
                kind = PropertyKind.Bounded;
                long? unit = PropertyUnit(id);
                foreach (string attribute in new[] { "UpperBoundValue", "LowerBoundValue", "SetPointValue" })
                {
                    var item = Get(id, attribute);
                    if (item == null || item.IsNull) continue;
                    dataType = TypeName(item);
                    var value = Wrapped(item);
                    if (IsNumber(value) && unit.HasValue && HasName(unit.Value)) value = StateValue.Of(Convert(value.Number, unit.Value));
                    values.Add(value);
                }
            }
            else if (IsA(id, "IfcPropertyTableValue"))
            {
                kind = PropertyKind.Table;
                columns = new List<TableColumn>();
                bool hasProject = HasProject();
                foreach (string attribute in new[] { "Defining", "Defined" })
                {
                    var items = Items(id, attribute + "Values");
                    if (items.Count == 0) continue;
                    long? unit = hasProject ? TableUnit(id, attribute) : null;
                    var column = new List<StateValue>();
                    foreach (var item in items)
                    {
                        var value = Wrapped(item);
                        if (IsNumber(value) && unit.HasValue && HasName(unit.Value)) value = StateValue.Of(Convert(value.Number, unit.Value));
                        column.Add(value);
                    }

                    columns.Add(new TableColumn(TypeName(items[0])!, column));
                }
            }
            else if (IsA(id, "IfcPropertyReferenceValue"))
            {
                kind = PropertyKind.Reference;
            }
            else if (IsA(id, "IfcComplexProperty") || IsA(id, "IfcPhysicalComplexQuantity"))
            {
                kind = PropertyKind.Complex;
            }
            else
            {
                kind = PropertyKind.Other;
            }

            return new PropertyState(name, kind, dataType, values, source, columns);
        }

        private static bool IsNumber(StateValue value) => value.Kind == ValueKind.Integer || value.Kind == ValueKind.Real;

        private IReadOnlyList<StepValue> Items(long id, string attribute)
        {
            var value = Get(id, attribute);
            return value != null && value.Kind == StepKind.List ? value.Items : Array.Empty<StepValue>();
        }

        private string? TypeName(StepValue value) =>
            value.Kind == StepKind.Typed ? value.TypeName : value.Kind == StepKind.Reference ? _file.TypeOf(value.Reference) : null;

        // ---- units (ifcopenshell.util.unit)
        private long? PropertyUnit(long id)
        {
            long? unit = Ref(id, "Unit");
            if (unit.HasValue) return unit;
            string? measure = null;
            if (IsA(id, "IfcPhysicalSimpleQuantity"))
            {
                measure = AllAttributes(_file.TypeOf(id)!)[3].DeclaredType;
            }
            else if (IsA(id, "IfcPropertySingleValue"))
            {
                var nominal = Get(id, "NominalValue");
                if (nominal != null && !nominal.IsNull) measure = TypeName(nominal);
            }
            else if (IsA(id, "IfcPropertyListValue"))
            {
                var items = Items(id, "ListValues");
                if (items.Count > 0) measure = TypeName(items[0]);
            }
            else if (IsA(id, "IfcPropertyBoundedValue"))
            {
                foreach (string attribute in new[] { "UpperBoundValue", "LowerBoundValue", "SetPointValue" })
                {
                    var item = Get(id, attribute);
                    if (item != null && !item.IsNull)
                    {
                        measure = TypeName(item);
                        break;
                    }
                }
            }

            return measure == null ? null : ProjectUnit(MeasureUnitType(measure));
        }

        private long? TableUnit(long id, string attribute)
        {
            long? unit = Ref(id, attribute + "Unit");
            if (unit.HasValue) return unit;
            var items = Items(id, attribute + "Values");
            return items.Count > 0 && TypeName(items[0]) is string measure ? ProjectUnit(MeasureUnitType(measure)) : null;
        }

        private string MeasureUnitType(string measure)
        {
            if (_measureUnitTypes.TryGetValue(measure, out var cached)) return cached;
            cached = MeasureUnitTypeOf(measure);
            _measureUnitTypes[measure] = cached;
            return cached;
        }

        private string MeasureUnitTypeOf(string measure)
        {
            // the STEP name comes in upper case; IfcOpenShell uses the schema name (CamelCase)
            string name = _catalog.TypeName(measure) ?? measure;
            if (name == "IfcNumericMeasure") return "USERDEFINED";
            foreach (string text in new[] { "Ifc", "Measure", "Non", "Positive", "Negative" }) name = name.Replace(text, "");
            return name.ToUpperInvariant() + "UNIT";
        }

        private bool HasProject() => _hasProject ??= _file.Ids.Any(i => IsA(i, "IfcProject"));

        private List<long>? ProjectUnits()
        {
            long project = _file.Ids.FirstOrDefault(i => IsA(i, "IfcProject"));
            if (project == 0) return null;
            long? assignment = Ref(project, "UnitsInContext");
            return assignment.HasValue ? Refs(assignment.Value, "Units").ToList() : null;
        }

        private long? ProjectUnit(string unitType)
        {
            if (_projectUnits.TryGetValue(unitType, out var cached)) return cached;
            long? found = null;
            var units = _units ??= ProjectUnits() ?? new List<long>();
            if (units != null)
            {
                foreach (long unit in units)
                {
                    if (Text(unit, "UnitType") == unitType)
                    {
                        found = unit;
                        break;
                    }
                }
            }

            _projectUnits[unitType] = found;
            return found;
        }

        private bool HasName(long unit) => !string.IsNullOrEmpty(Text(unit, "Name"));

        /// <summary>dump_state.convert: file unit to SI.</summary>
        private double Convert(double value, long unit, bool massInKilo = false)
        {
            string? name = Text(unit, "Name");
            if (string.IsNullOrEmpty(name)) return value;
            string? unitType = Text(unit, "UnitType");
            if (unitType == null || !SiTypeNames.Contains(unitType)) return value;
            string? toPrefix = massInKilo && unitType == "MASSUNIT" ? "KILO" : null;
            string? fromPrefix = Text(unit, "Prefix");

            if (SiConversions.TryGetValue(name!.ToLowerInvariant(), out double factor))
            {
                value *= factor;
            }
            else if (!string.IsNullOrEmpty(fromPrefix))
            {
                value *= PrefixMultiplier(fromPrefix);
                if (name.Contains("SQUARE"))
                {
                    value *= PrefixMultiplier(fromPrefix);
                }
                else if (name.Contains("CUBIC"))
                {
                    value *= PrefixMultiplier(fromPrefix);
                    value *= PrefixMultiplier(fromPrefix);
                }
            }

            if (toPrefix != null)
            {
                value *= 1 / PrefixMultiplier(toPrefix);
                if (name.Contains("SQUARE"))
                {
                    value *= 1 / PrefixMultiplier(toPrefix);
                }
                else if (name.Contains("CUBIC"))
                {
                    value *= 1 / PrefixMultiplier(toPrefix);
                    value *= 1 / PrefixMultiplier(toPrefix);
                }
            }

            return value;
        }

        private static double PrefixMultiplier(string? text)
        {
            if (string.IsNullOrEmpty(text)) return 1;
            string upper = text!.ToUpperInvariant();
            foreach (var (prefix, factor) in Prefixes)
            {
                if (upper.Contains(prefix)) return factor;
            }

            return 1;
        }

        // ---- classification (ifcopenshell.util.classification)
        private IReadOnlyList<ClassificationState> Classifications(long id)
        {
            List<long> leaves;
            try
            {
                leaves = References(id);
            }
            catch (InvalidDataException)
            {
                return Array.Empty<ClassificationState>();
            }

            var result = new List<ClassificationState>();
            foreach (long leaf in leaves)
            {
                var values = new List<string>();
                var chain = new List<long> { leaf };
                chain.AddRange(InheritedReferences(leaf).Where(r => r != leaf));
                foreach (long reference in chain)
                {
                    if (IsA(reference, "IfcClassification")) continue;
                    string? value = Text(reference, "Identification", out bool exists);
                    if (!exists) value = Text(reference, "ItemReference");
                    if (value != null) values.Add(value);
                }

                long? system;
                try
                {
                    system = ClassificationOf(leaf);
                }
                catch (InvalidDataException)
                {
                    system = null;
                }

                result.Add(new ClassificationState(system.HasValue ? Text(system.Value, "Name") : null, values));
            }

            result.Sort(CompareClassifications);
            return result;
        }

        private static int CompareClassifications(ClassificationState a, ClassificationState b)
        {
            int compare = string.CompareOrdinal(a.System ?? "", b.System ?? "");
            if (compare != 0) return compare;
            for (int i = 0; i < Math.Min(a.Values.Count, b.Values.Count); i++)
            {
                compare = string.CompareOrdinal(a.Values[i], b.Values[i]);
                if (compare != 0) return compare;
            }

            return a.Values.Count.CompareTo(b.Values.Count);
        }

        private List<long> References(long id)
        {
            var typeResults = new List<long>();
            if (IsA(id, "IfcObject"))
            {
                long? type = TypeOf(id);
                if (type.HasValue && type.Value != id) typeResults = References(type.Value);
            }

            var occurrence = new List<long>();
            foreach (long rel in Inverse(id, "HasAssociations"))
            {
                if (!IsA(rel, "IfcRelAssociatesClassification")) continue;
                long? relating = Ref(rel, "RelatingClassification");
                if (relating.HasValue && !occurrence.Contains(relating.Value)) occurrence.Add(relating.Value);
            }

            if (typeResults.Count == 0) return occurrence;

            // the occurrence overrides the type per classification system
            var bySystem = new List<(long? System, List<long> References)>();
            void Put(long reference, bool replace, HashSet<long?> replaced)
            {
                long? system = ClassificationOf(reference);
                int index = bySystem.FindIndex(e => e.System == system);
                if (index < 0)
                {
                    bySystem.Add((system, new List<long> { reference }));
                    replaced.Add(system);
                }
                else if (replace && !replaced.Contains(system))
                {
                    bySystem[index] = (system, new List<long> { reference });
                    replaced.Add(system);
                }
                else
                {
                    bySystem[index].References.Add(reference);
                }
            }

            var none = new HashSet<long?>();
            foreach (long reference in typeResults) Put(reference, false, none);
            var replacedSystems = new HashSet<long?>();
            foreach (long reference in occurrence) Put(reference, true, replacedSystems);
            var results = new List<long>();
            foreach (var entry in bySystem)
            {
                foreach (long reference in entry.References)
                {
                    if (!results.Contains(reference)) results.Add(reference);
                }
            }

            return results;
        }

        private IEnumerable<long> InheritedReferences(long reference)
        {
            long? current = reference;
            for (int depth = 0; current.HasValue && depth < 64; depth++)
            {
                if (IsA(current.Value, "IfcClassification")) yield break;
                yield return current.Value;
                current = Ref(current.Value, "ReferencedSource");
            }
        }

        private long? ClassificationOf(long reference)
        {
            long? current = reference;
            for (int depth = 0; current.HasValue && depth < 64; depth++)
            {
                if (IsA(current.Value, "IfcClassification")) return current;
                if (Get(current.Value, "ReferencedSource") == null) throw new InvalidDataException("no ReferencedSource");
                current = Ref(current.Value, "ReferencedSource");
            }

            return null;
        }

        // ---- material
        private MaterialState? Material(long id)
        {
            long? found = MaterialOf(id);
            if (!found.HasValue) return null;
            long material = found.Value;
            var values = new SortedSet<string>(StringComparer.Ordinal);

            void Add(long item, params string[] attributes)
            {
                foreach (string attribute in attributes)
                {
                    var value = Get(item, attribute);
                    if (value != null && value.Kind == StepKind.String) values.Add(value.Text!);
                }
            }

            void AddMaterial(long? item)
            {
                if (item.HasValue) Add(item.Value, "Name", "Category");
            }

            if (IsA(material, "IfcMaterial"))
            {
                Add(material, "Name", "Category");
            }
            else if (IsA(material, "IfcMaterialList"))
            {
                foreach (long item in Refs(material, "Materials")) Add(item, "Name", "Category");
            }
            else if (IsA(material, "IfcMaterialLayerSet"))
            {
                Add(material, "LayerSetName");
                foreach (long item in Refs(material, "MaterialLayers"))
                {
                    Add(item, "Name", "Category");
                    AddMaterial(Ref(item, "Material"));
                }
            }
            else if (IsA(material, "IfcMaterialProfileSet"))
            {
                Add(material, "Name");
                foreach (long item in Refs(material, "MaterialProfiles"))
                {
                    Add(item, "Name", "Category");
                    AddMaterial(Ref(item, "Material"));
                }
            }
            else if (IsA(material, "IfcMaterialConstituentSet"))
            {
                Add(material, "Name");
                foreach (long item in Refs(material, "MaterialConstituents"))
                {
                    Add(item, "Name", "Category");
                    AddMaterial(Ref(item, "Material"));
                }
            }

            return new MaterialState(values.ToList());
        }

        private long? MaterialOf(long id, bool inherit = true)
        {
            var associations = Inverse(id, "HasAssociations");
            foreach (long rel in associations)
            {
                if (!IsA(rel, "IfcRelAssociatesMaterial")) continue;
                long? relating = Ref(rel, "RelatingMaterial");
                if (relating.HasValue && IsA(relating.Value, "IfcMaterialLayerSetUsage")) return Ref(relating.Value, "ForLayerSet");
                if (relating.HasValue && IsA(relating.Value, "IfcMaterialProfileSetUsage")) return Ref(relating.Value, "ForProfileSet");
                return relating;
            }

            if (inherit)
            {
                long? type = TypeOf(id);
                if (type.HasValue && type.Value != id && Inverse(type.Value, "HasAssociations").Count > 0) return MaterialOf(type.Value);
            }

            return null;
        }

        // ---- relationships
        private ElementRelations Relations(long id)
        {
            var groups = new List<string>();
            foreach (long rel in Inverse(id, "HasAssignments"))
            {
                if (!IsA(rel, "IfcRelAssignsToGroup")) continue;
                long? group = Ref(rel, "RelatingGroup");
                if (group.HasValue) groups.Add(Id(group));
            }

            string? voided = null;
            string? filled = null;
            if (IsA(id, "IfcOpeningElement"))
            {
                var voids = Inverse(id, "VoidsElements");
                voided = voids.Count > 0 ? IdOrNull(Ref(voids[0], "RelatingBuildingElement")) : null;
            }
            else
            {
                var fills = Inverse(id, "FillsVoids");
                filled = fills.Count > 0 ? IdOrNull(Ref(fills[0], "RelatingOpeningElement")) : null;
            }

            var contained = Inverse(id, "ContainedInStructure");
            string? container = contained.Count > 0 ? IdOrNull(Ref(contained[0], "RelatingStructure")) : null;

            string? aggregate = null;
            string? nest = null;
            var decomposes = Inverse(id, "Decomposes");
            if (decomposes.Count > 0)
            {
                if (!_is2X3 || IsA(decomposes[0], "IfcRelAggregates")) aggregate = IdOrNull(Ref(decomposes[0], "RelatingObject"));
                if (_is2X3 && IsA(decomposes[0], "IfcRelNests")) nest = IdOrNull(Ref(decomposes[0], "RelatingObject"));
            }

            if (!_is2X3)
            {
                var nests = Inverse(id, "Nests");
                if (nests.Count > 0) nest = IdOrNull(Ref(nests[0], "RelatingObject"));
            }

            return new ElementRelations(container, aggregate, nest, groups, filled, voided);
        }

        private static string Id(long? id) => id!.Value.ToString(CultureInfo.InvariantCulture);

        private static string? IdOrNull(long? id) => id.HasValue ? Id(id) : null;
    }
}
