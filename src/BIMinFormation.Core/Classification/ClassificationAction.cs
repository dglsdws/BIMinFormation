using System;
using System.Collections.Generic;
using System.Linq;
using BIMinFormation.Ids;
using BIMinFormation.Rules;

namespace BIMinFormation.Core.Classification
{
    /// <summary>A property written by the Classification (e.g. <c>BIMPR.EOI = PR.11.05 PAREDE</c>).</summary>
    public sealed class IdentityWrite
    {
        public string PropertySet { get; }
        public string Property { get; }
        public string Value { get; }
        public string? DataType { get; }

        public IdentityWrite(string propertySet, string property, string value, string? dataType)
        {
            PropertySet = propertySet;
            Property = property;
            Value = value;
            DataType = dataType;
        }

        public override string ToString() => PropertySet + "." + Property + " = " + Value;
    }

    /// <summary>
    /// What the Classification writes on an element so that a specification applies to it: the export entity
    /// (<c>IFC_EXPORT_ELEMENT_AS</c>), the predefined type and the applicability identity (an identifier property such
    /// as the BIMPR EOI, an attribute or a classification). Values are always exact, never typed by the user.
    /// </summary>
    public sealed class ClassificationAction
    {
        public Specification Specification { get; }

        /// <summary>Entity spelled as in the schema (e.g. <c>IfcWall</c>); <c>null</c> if the specification does not define one.</summary>
        public string? Entity { get; }

        /// <summary>Predefined type to write (<c>USERDEFINED</c> in the fallback for types the schema lacks).</summary>
        public string? PredefinedType { get; }

        /// <summary><c>IfcObjectType</c> when the predefined type does not exist in the schema (fallback).</summary>
        public string? ObjectType { get; }

        public IReadOnlyList<IdentityWrite> Properties { get; }

        /// <summary>Identity attributes (name → value), written through the override parameters.</summary>
        public IReadOnlyDictionary<string, string> Attributes { get; }

        /// <summary>Classification codes in the exporter format: <c>[System]Code</c>.</summary>
        public IReadOnlyList<string> ClassificationCodes { get; }

        /// <summary>Why the specification cannot be applied automatically (empty when it can).</summary>
        public IReadOnlyList<string> Problems { get; }

        public bool CanApply => Problems.Count == 0;

        /// <summary>
        /// Predefined types accepted when the specification gives a list (e.g. slab FLOOR, ROOF…): the user picks one in the
        /// window through <see cref="WithPredefinedType"/>. <c>null</c> when the specification fixes a single value or none.
        /// </summary>
        public IReadOnlyList<string>? PredefinedTypeChoices { get; }

        /// <summary>The predefined type still has to be picked from the specification list.</summary>
        public bool NeedsPredefinedChoice => PredefinedTypeChoices != null && PredefinedType == null;

        private readonly IfcSchemaCatalog _catalog;

        private ClassificationAction(Specification specification, IfcSchemaCatalog catalog, string? entity, string? predefinedType,
            string? objectType, IReadOnlyList<string>? choices, IReadOnlyList<IdentityWrite> properties,
            IReadOnlyDictionary<string, string> attributes, IReadOnlyList<string> classificationCodes, IReadOnlyList<string> problems)
        {
            Specification = specification;
            _catalog = catalog;
            Entity = entity;
            PredefinedType = predefinedType;
            ObjectType = objectType;
            PredefinedTypeChoices = choices;
            Properties = properties;
            Attributes = attributes;
            ClassificationCodes = classificationCodes;
            Problems = problems;
        }

        /// <summary>The same action with the predefined type picked by the user (it must be in the specification list).</summary>
        public ClassificationAction WithPredefinedType(string choice)
        {
            if (PredefinedTypeChoices == null || !PredefinedTypeChoices.Contains(choice))
            {
                throw new ArgumentException("Predefined type outside the specification list: " + choice, nameof(choice));
            }

            var resolved = Resolve(_catalog, Entity, choice);
            return new ClassificationAction(Specification, _catalog, Entity, resolved.Predefined, resolved.ObjectType,
                PredefinedTypeChoices, Properties, Attributes, ClassificationCodes, Problems);
        }

        /// <summary>Predefined type the schema lacks → USERDEFINED + ObjectType with the name (as the BIM PR protocol requires).</summary>
        private static (string Predefined, string? ObjectType) Resolve(IfcSchemaCatalog catalog, string? entity, string value)
        {
            var allowed = entity == null ? null : catalog.PredefinedTypes(entity);
            return allowed != null && !allowed.Contains(value) && value != "USERDEFINED"
                ? ("USERDEFINED", value)
                : (value, null);
        }

        /// <summary>Value of the <c>IFC_EXPORT_ELEMENT_AS</c> parameter (the entity; the predefined type goes separately).</summary>
        public string? ExportAs => Entity;

        public static ClassificationAction For(Specification specification, IfcSchemaVersion schema)
        {
            var catalog = IfcSchemaCatalog.For(schema);
            var problems = new List<string>();
            var properties = new List<IdentityWrite>();
            var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
            var codes = new List<string>();

            foreach (var facet in specification.Applicability.Facets)
            {
                switch (facet)
                {
                    case PropertyFacet p when p.PropertySet is SimpleValue pset && p.BaseName is SimpleValue name:
                        if (p.Value is SimpleValue value)
                        {
                            properties.Add(new IdentityWrite(pset.Text, name.Text, value.Text, p.DataType));
                        }
                        else if (p.Value != null)
                        {
                            problems.Add(Tr.T("O applicability pede ") + pset.Text + "." + name.Text + Tr.T(" por restrição: escolha o valor no Preencher."));
                        }
                        else
                        {
                            problems.Add(Tr.T("O applicability pede que ") + pset.Text + "." + name.Text + Tr.T(" exista: preencha no Preencher."));
                        }

                        break;
                    case AttributeFacet a when a.Name is SimpleValue attributeName && a.Value is SimpleValue attributeValue:
                        attributes[attributeName.Text] = attributeValue.Text;
                        break;
                    case ClassificationFacet c when c.Value is SimpleValue code:
                        codes.Add((c.System is SimpleValue system ? "[" + system.Text + "]" : "") + code.Text);
                        break;
                    case EntityFacet _:
                        break; // the entity is handled below
                    default:
                        problems.Add(Tr.T("Faceta ") + facet.Kind + Tr.T(" no applicability não pode ser gravada pela Classificação."));
                        break;
                }
            }

            // the entity comes from the requirements (e.g. BIMPR) or from the applicability (e.g. Luxembourg, NL)
            var entityFacets = specification.Requirements.Facets.OfType<EntityFacet>()
                .Concat(specification.Applicability.Facets.OfType<EntityFacet>())
                .ToList();
            string? entity = null;
            string? predefined = null;
            string? objectType = null;
            IReadOnlyList<string>? choices = null;
            var entityFacet = entityFacets.FirstOrDefault();
            if (entityFacet != null)
            {
                if (entityFacet.Name is SimpleValue simple)
                {
                    entity = catalog.Entity(simple.Text)?.Name ?? simple.Text;
                }
                else
                {
                    problems.Add(Tr.T("A ficha aceita mais de uma entidade: escolha a entidade à mão."));
                }

                if (entityFacet.PredefinedType is SimpleValue predefinedValue)
                {
                    var resolved = Resolve(catalog, entity, predefinedValue.Text);
                    predefined = resolved.Predefined;
                    objectType = resolved.ObjectType;
                }
                else if (entityFacet.PredefinedType is RestrictionValue restriction)
                {
                    choices = restriction.ClosedList();
                    if (choices == null)
                    {
                        problems.Add(Tr.T("O tipo predefinido da ficha é um padrão aberto: preencha à mão."));
                    }
                }
            }

            if (properties.Count == 0 && attributes.Count == 0 && codes.Count == 0 && entity == null)
            {
                problems.Add(Tr.T("A ficha não tem identidade que a Classificação possa gravar."));
            }

            return new ClassificationAction(specification, catalog, entity, predefined, objectType, choices, properties, attributes,
                codes, problems);
        }

        /// <summary>The element state after applying the action (to check it before writing).</summary>
        public ElementState ApplyTo(ElementState element)
        {
            var sets = element.PropertySets.ToDictionary(p => p.Name, p => p.Properties.ToList(), StringComparer.Ordinal);
            foreach (var write in Properties)
            {
                if (!sets.TryGetValue(write.PropertySet, out var list))
                {
                    list = new List<PropertyState>();
                    sets[write.PropertySet] = list;
                }

                list.RemoveAll(p => p.Name == write.Property);
                list.Add(new PropertyState(write.Property, PropertyKind.Single, write.DataType ?? "IFCLABEL",
                    new[] { TypedValue.FromText(write.Value, write.DataType) }));
            }

            var attributes = element.Attributes.ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal);
            foreach (var attribute in Attributes)
            {
                attributes[attribute.Key] = StateValue.Of(attribute.Value);
            }

            if (ObjectType != null)
            {
                attributes["ObjectType"] = StateValue.Of(ObjectType);
            }

            var classifications = element.Classifications.ToList();
            foreach (string code in ClassificationCodes)
            {
                int close = code.IndexOf(']');
                string? system = code.StartsWith("[", StringComparison.Ordinal) && close > 0 ? code.Substring(1, close - 1) : null;
                classifications.Add(new ClassificationState(system, new[] { close > 0 ? code.Substring(close + 1) : code }));
            }

            string entity = Entity?.ToUpperInvariant() ?? element.Entity;
            string? predefinedType = ObjectType ?? PredefinedType ?? element.PredefinedType;
            bool userDefined = ObjectType != null || (PredefinedType == null && element.IsUserDefined);
            return new ElementState(element.Id, entity, element.TypeEntity, predefinedType, userDefined, attributes,
                sets.Select(s => new PropertySetState(s.Key, s.Value)).ToList(), classifications, element.Material, element.Relations);
        }
    }
}
