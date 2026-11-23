namespace BIMinFormation.Ids
{
    public enum FacetKind
    {
        Entity,
        Attribute,
        Property,
        Classification,
        Material,
        PartOf,
    }

    /// <summary>
    /// Cardinality of a facet in <c>requirements</c>. In the <c>applicability</c> every facet is treated as
    /// <see cref="Required"/>.
    /// </summary>
    public enum FacetCardinality
    {
        Required,
        Optional,
        Prohibited,
    }

    /// <summary>IDS 1.0 facet, used both in the applicability and in the requirements.</summary>
    public abstract class Facet
    {
        public abstract FacetKind Kind { get; }

        /// <summary>Default <see cref="FacetCardinality.Required"/> (IDS 1.0).</summary>
        public FacetCardinality Cardinality { get; }

        public string? Instructions { get; }

        public string? Uri { get; }

        public int Line { get; }

        protected Facet(FacetCardinality cardinality, string? instructions, string? uri, int line)
        {
            Cardinality = cardinality;
            Instructions = instructions;
            Uri = uri;
            Line = line;
        }
    }

    public sealed class EntityFacet : Facet
    {
        public override FacetKind Kind => FacetKind.Entity;

        public IdsValue Name { get; }

        public IdsValue? PredefinedType { get; }

        public EntityFacet(IdsValue name, IdsValue? predefinedType, string? instructions = null, int line = 0)
            : base(FacetCardinality.Required, instructions, null, line)
        {
            Name = name;
            PredefinedType = predefinedType;
        }
    }

    public sealed class AttributeFacet : Facet
    {
        public override FacetKind Kind => FacetKind.Attribute;

        public IdsValue Name { get; }

        public IdsValue? Value { get; }

        public AttributeFacet(IdsValue name, IdsValue? value, FacetCardinality cardinality = FacetCardinality.Required,
            string? instructions = null, int line = 0)
            : base(cardinality, instructions, null, line)
        {
            Name = name;
            Value = value;
        }
    }

    public sealed class PropertyFacet : Facet
    {
        public override FacetKind Kind => FacetKind.Property;

        public IdsValue PropertySet { get; }

        public IdsValue BaseName { get; }

        public IdsValue? Value { get; }

        /// <summary>IFC type in upper case (e.g. <c>IFCLABEL</c>), or <c>null</c> if missing.</summary>
        public string? DataType { get; }

        public PropertyFacet(IdsValue propertySet, IdsValue baseName, IdsValue? value, string? dataType,
            FacetCardinality cardinality = FacetCardinality.Required, string? instructions = null, string? uri = null,
            int line = 0)
            : base(cardinality, instructions, uri, line)
        {
            PropertySet = propertySet;
            BaseName = baseName;
            Value = value;
            DataType = dataType;
        }
    }

    public sealed class ClassificationFacet : Facet
    {
        public override FacetKind Kind => FacetKind.Classification;

        public IdsValue? Value { get; }

        public IdsValue? System { get; }

        public ClassificationFacet(IdsValue? value, IdsValue? system,
            FacetCardinality cardinality = FacetCardinality.Required, string? instructions = null, string? uri = null,
            int line = 0)
            : base(cardinality, instructions, uri, line)
        {
            Value = value;
            System = system;
        }
    }

    public sealed class MaterialFacet : Facet
    {
        public override FacetKind Kind => FacetKind.Material;

        public IdsValue? Value { get; }

        public MaterialFacet(IdsValue? value, FacetCardinality cardinality = FacetCardinality.Required,
            string? instructions = null, string? uri = null, int line = 0)
            : base(cardinality, instructions, uri, line)
        {
            Value = value;
        }
    }

    public sealed class PartOfFacet : Facet
    {
        public override FacetKind Kind => FacetKind.PartOf;

        public EntityFacet Entity { get; }

        /// <summary>E.g. <c>IFCRELAGGREGATES</c>; <c>null</c> = any relationship.</summary>
        public string? Relation { get; }

        public PartOfFacet(EntityFacet entity, string? relation,
            FacetCardinality cardinality = FacetCardinality.Required, string? instructions = null, int line = 0)
            : base(cardinality, instructions, null, line)
        {
            Entity = entity;
            Relation = relation;
        }
    }
}
