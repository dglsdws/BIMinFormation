using System;
using System.Collections.Generic;
using System.Globalization;
using BIMinFormation.Ids;

namespace BIMinFormation.Rules
{
    public enum ValueKind
    {
        /// <summary>Missing attribute or value (<c>$</c>).</summary>
        Null,
        String,
        Integer,
        Real,
        Boolean,
        /// <summary>IfcLogical UNKNOWN: treated as empty by the IDS.</summary>
        Unknown,
        /// <summary>Reference to another entity: not comparable with IDS values.</summary>
        Entity,
        /// <summary>List or set; <see cref="StateValue.Count"/> keeps the size.</summary>
        Aggregate,
    }

    /// <summary>
    /// Value of an attribute or property, as the IDS sees it. Numbers with a unit are already in SI (the IDS fixes the
    /// default units).
    /// </summary>
    public sealed class StateValue
    {
        public ValueKind Kind { get; }
        public string? Text { get; }
        public double Number { get; }
        public long Integer { get; }
        public bool Boolean { get; }
        public int Count { get; }

        private StateValue(ValueKind kind, string? text = null, double number = 0, long integer = 0,
            bool boolean = false, int count = 0)
        {
            Kind = kind;
            Text = text;
            Number = number;
            Integer = integer;
            Boolean = boolean;
            Count = count;
        }

        public static StateValue Null { get; } = new StateValue(ValueKind.Null);
        public static StateValue Unknown { get; } = new StateValue(ValueKind.Unknown);
        public static StateValue EntityReference { get; } = new StateValue(ValueKind.Entity);

        public static StateValue Of(string text) => new StateValue(ValueKind.String, text: text);
        public static StateValue Of(double number) => new StateValue(ValueKind.Real, number: number);
        public static StateValue Of(long integer) => new StateValue(ValueKind.Integer, integer: integer, number: integer);
        public static StateValue Of(bool value) => new StateValue(ValueKind.Boolean, boolean: value);
        public static StateValue Aggregate(int count) => new StateValue(ValueKind.Aggregate, count: count);

        /// <summary>Null, empty text, empty list or logical UNKNOWN: the IDS treats it as "no value".</summary>
        public bool IsEmpty =>
            Kind == ValueKind.Null
            || Kind == ValueKind.Unknown
            || (Kind == ValueKind.String && Text!.Length == 0)
            || (Kind == ValueKind.Aggregate && Count == 0);

        public override string ToString()
        {
            switch (Kind)
            {
                case ValueKind.String:
                    return Text!;
                case ValueKind.Integer:
                    return Integer.ToString(CultureInfo.InvariantCulture);
                case ValueKind.Real:
                    return Number.ToString("R", CultureInfo.InvariantCulture);
                case ValueKind.Boolean:
                    return Boolean ? "true" : "false";
                case ValueKind.Aggregate:
                    return "(" + Count + Tr.T(" itens)");
                default:
                    return Kind.ToString().ToUpperInvariant();
            }
        }
    }

    public enum PropertyKind
    {
        Single,
        Quantity,
        Enumerated,
        List,
        Bounded,
        Table,
        /// <summary>Attribute of an IfcPreDefinedPropertySet (e.g. IfcDoorLiningProperties).</summary>
        Predefined,
        /// <summary>IfcPropertyReferenceValue: not supported by IDS 1.0.</summary>
        Reference,
        /// <summary>IfcComplexProperty / IfcPhysicalComplexQuantity: not supported by IDS 1.0.</summary>
        Complex,
        Other,
    }

    public enum PropertySource
    {
        Occurrence,
        Type,
    }

    /// <summary>Column of an IfcPropertyTableValue.</summary>
    public sealed class TableColumn
    {
        public string DataType { get; }
        public IReadOnlyList<StateValue> Values { get; }

        public TableColumn(string dataType, IReadOnlyList<StateValue> values)
        {
            DataType = dataType;
            Values = values;
        }
    }

    public sealed class PropertyState
    {
        public string Name { get; }
        public PropertyKind Kind { get; }

        /// <summary>IFC type in upper case (e.g. <c>IFCLABEL</c>); <c>null</c> if unknown.</summary>
        public string? DataType { get; }

        public IReadOnlyList<StateValue> Values { get; }
        public IReadOnlyList<TableColumn> Columns { get; }
        public PropertySource Source { get; }

        public PropertyState(string name, PropertyKind kind, string? dataType, IReadOnlyList<StateValue> values,
            PropertySource source = PropertySource.Occurrence, IReadOnlyList<TableColumn>? columns = null)
        {
            Name = name;
            Kind = kind;
            DataType = dataType;
            Values = values;
            Source = source;
            Columns = columns ?? Array.Empty<TableColumn>();
        }

        /// <summary>Has at least one non-empty value (or is of a type the IDS does not read).</summary>
        public bool HasValue
        {
            get
            {
                if (Kind == PropertyKind.Table)
                {
                    foreach (var column in Columns)
                    {
                        foreach (var value in column.Values)
                        {
                            if (!value.IsEmpty) return true;
                        }
                    }

                    return false;
                }

                if (Kind == PropertyKind.Reference || Kind == PropertyKind.Complex || Kind == PropertyKind.Other)
                {
                    return true;
                }

                foreach (var value in Values)
                {
                    if (!value.IsEmpty) return true;
                }

                return false;
            }
        }
    }

    public sealed class PropertySetState
    {
        public string Name { get; }
        public IReadOnlyList<PropertyState> Properties { get; }

        public PropertySetState(string name, IReadOnlyList<PropertyState> properties)
        {
            Name = name;
            Properties = properties;
        }
    }

    /// <summary>
    /// A classification reference already resolved: the system and the codes of the reference and of every parent
    /// reference. A "light" classification (without a reference) has no codes.
    /// </summary>
    public sealed class ClassificationState
    {
        public string? System { get; }
        public IReadOnlyList<string> Values { get; }

        public ClassificationState(string? system, IReadOnlyList<string> values)
        {
            System = system;
            Values = values;
        }
    }

    /// <summary>Effective material: names and categories of the material or of the set and its items.</summary>
    public sealed class MaterialState
    {
        public IReadOnlyList<string> Values { get; }

        public MaterialState(IReadOnlyList<string> values)
        {
            Values = values;
        }
    }

    /// <summary>Direct relationships of an element, by identifier of the related element.</summary>
    public sealed class ElementRelations
    {
        public string? Container { get; }
        public string? Aggregate { get; }
        public string? Nest { get; }
        public IReadOnlyList<string> Groups { get; }

        /// <summary>Opening this element fills (IfcRelFillsElement).</summary>
        public string? FillsVoid { get; }

        /// <summary>Element this opening voids (IfcRelVoidsElement).</summary>
        public string? Voids { get; }

        public ElementRelations(string? container = null, string? aggregate = null, string? nest = null,
            IReadOnlyList<string>? groups = null, string? fillsVoid = null, string? voids = null)
        {
            Container = container;
            Aggregate = aggregate;
            Nest = nest;
            Groups = groups ?? Array.Empty<string>();
            FillsVoid = fillsVoid;
            Voids = voids;
        }

        public static ElementRelations None { get; } = new ElementRelations();
    }

    /// <summary>
    /// What the IDS sees of an element, already resolved: effective predefined type, properties inherited from the type,
    /// classifications (the occurrence overrides the type per system) and material. Comes from the IFC or from the Revit
    /// model, as the exporter will write it.
    /// </summary>
    public sealed class ElementState
    {
        public string Id { get; }

        /// <summary>IFC class in upper case (e.g. <c>IFCWALL</c>).</summary>
        public string Entity { get; }

        /// <summary>Class of the related type (e.g. <c>IFCWALLTYPE</c>), used in IFC2X3.</summary>
        public string? TypeEntity { get; }

        /// <summary>Effective predefined type; for USERDEFINED, the ObjectType/ElementType.</summary>
        public string? PredefinedType { get; }

        public bool IsUserDefined { get; }

        public IReadOnlyDictionary<string, StateValue> Attributes { get; }
        public IReadOnlyList<PropertySetState> PropertySets { get; }
        public IReadOnlyList<ClassificationState> Classifications { get; }
        public MaterialState? Material { get; }
        public ElementRelations Relations { get; }

        /// <summary>IFC GlobalId (from the exporter, in the Revit model); <c>null</c> if there is none.</summary>
        public string? GlobalId =>
            Attributes.TryGetValue("GlobalId", out var value) && value.Kind == ValueKind.String ? value.Text : null;

        public ElementState(string id, string entity, string? typeEntity = null, string? predefinedType = null,
            bool isUserDefined = false, IReadOnlyDictionary<string, StateValue>? attributes = null,
            IReadOnlyList<PropertySetState>? propertySets = null,
            IReadOnlyList<ClassificationState>? classifications = null, MaterialState? material = null,
            ElementRelations? relations = null)
        {
            Id = id;
            Entity = entity;
            TypeEntity = typeEntity;
            PredefinedType = predefinedType;
            IsUserDefined = isUserDefined;
            Attributes = attributes ?? new Dictionary<string, StateValue>();
            PropertySets = propertySets ?? Array.Empty<PropertySetState>();
            Classifications = classifications ?? Array.Empty<ClassificationState>();
            Material = material;
            Relations = relations ?? ElementRelations.None;
        }

        public override string ToString() => Entity + " #" + Id;
    }

    /// <summary>Set of elements evaluated together (an IFC, or the Revit model).</summary>
    public sealed class ModelState
    {
        private readonly Dictionary<string, ElementState> _byId;

        public IfcSchemaVersion Schema { get; }
        public IReadOnlyList<ElementState> Elements { get; }

        public ModelState(IfcSchemaVersion schema, IReadOnlyList<ElementState> elements)
        {
            Schema = schema;
            Elements = elements;
            _byId = new Dictionary<string, ElementState>(StringComparer.Ordinal);
            foreach (var element in elements)
            {
                _byId[element.Id] = element;
            }
        }

        public ElementState? Find(string? id) =>
            id != null && _byId.TryGetValue(id, out var element) ? element : null;
    }
}
