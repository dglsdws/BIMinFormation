using System;
using System.Collections.Generic;
using System.Linq;
using BIMinFormation.Ids;

namespace BIMinFormation.Rules
{
    /// <summary>
    /// A consolidated requirement for an element: the same property (or attribute, entity…) asked by one or more applicable
    /// specifications.
    /// </summary>
    public sealed class PlannedRequirement
    {
        internal PlannedRequirement(FacetKind kind, string key, string? propertySet, string? name)
        {
            Kind = kind;
            Key = key;
            PropertySet = propertySet;
            Name = name;
        }

        public FacetKind Kind { get; }

        /// <summary>Consolidation key (e.g. <c>property:BIMPR/Tipo</c>).</summary>
        public string Key { get; }

        /// <summary>Pset of the property; <c>null</c> for the other facets.</summary>
        public string? PropertySet { get; }

        /// <summary>Name of the property or attribute; the entity or the system for the other facets.</summary>
        public string? Name { get; }

        public string? DataType { get; internal set; }

        /// <summary>Predefined type restrictions (only in <see cref="FacetKind.Entity"/>).</summary>
        public List<IdsValue> PredefinedTypes { get; } = new List<IdsValue>();

        /// <summary>Predefined types accepted by every specification; <c>null</c> when free.</summary>
        public IReadOnlyList<string>? AllowedPredefinedTypes { get; internal set; }

        /// <summary><c>required</c> beats <c>optional</c>; <c>prohibited</c> with another cardinality is a conflict.</summary>
        public FacetCardinality Cardinality { get; internal set; } = FacetCardinality.Optional;

        /// <summary>Value restrictions, all required at the same time.</summary>
        public List<IdsValue> Values { get; } = new List<IdsValue>();

        /// <summary>
        /// Allowed values when some restriction is a closed list (intersection of all); <c>null</c> when the value is free.
        /// </summary>
        public IReadOnlyList<string>? AllowedValues { get; internal set; }

        public List<Facet> Facets { get; } = new List<Facet>();

        public List<Specification> Sources { get; } = new List<Specification>();

        public override string ToString() =>
            Key + (DataType != null ? " [" + DataType + "]" : "") + " " + Cardinality
            + (AllowedValues != null ? " {" + string.Join("|", AllowedValues) + "}" : "");
    }

    public enum ConflictReason
    {
        /// <summary>Different data types for the same property.</summary>
        DataType,
        /// <summary>No value meets every restriction.</summary>
        Value,
        /// <summary>One specification requires it and another prohibits it.</summary>
        Cardinality,
        /// <summary>Different entities (or predefined types) required for the same element.</summary>
        Entity,
    }

    public sealed class PlanConflict
    {
        public PlannedRequirement Requirement { get; }
        public ConflictReason Reason { get; }
        public string Detail { get; }

        public PlanConflict(PlannedRequirement requirement, ConflictReason reason, string detail)
        {
            Requirement = requirement;
            Reason = reason;
            Detail = detail;
        }

        public override string ToString() =>
            Reason + " em " + Requirement.Key + ": " + Detail + " ("
            + string.Join(", ", Requirement.Sources.Select(s => s.Name).Distinct()) + ")";
    }

    /// <summary>What an element must have, adding up every specification applicable to it.</summary>
    public sealed class ElementPlan
    {
        public ElementState Element { get; }
        public IReadOnlyList<Specification> Specifications { get; }
        public IReadOnlyList<PlannedRequirement> Requirements { get; }
        public IReadOnlyList<PlanConflict> Conflicts { get; }

        /// <summary>Facets that cannot be consolidated (Pset or property name given by a restriction).</summary>
        public IReadOnlyList<Facet> Unplanned { get; }

        public ElementPlan(ElementState element, IReadOnlyList<Specification> specifications,
            IReadOnlyList<PlannedRequirement> requirements, IReadOnlyList<PlanConflict> conflicts,
            IReadOnlyList<Facet> unplanned)
        {
            Element = element;
            Specifications = specifications;
            Requirements = requirements;
            Conflicts = conflicts;
            Unplanned = unplanned;
        }

        public PlannedRequirement? Find(string propertySet, string name) =>
            Requirements.FirstOrDefault(r =>
                r.Kind == FacetKind.Property && r.PropertySet == propertySet && r.Name == name);
    }

    /// <summary>
    /// Merges the requirements of every specification applicable to an element into a single plan and points out conflicts
    /// (the same property with incompatible type, value or cardinality).
    /// </summary>
    public static class RequirementPlanner
    {
        public static ElementPlan Plan(ElementState element, IEnumerable<Specification> specifications, ModelState model)
        {
            var applicable = specifications
                .Where(s => s.Usage != SpecificationUsage.Prohibited && SpecificationEvaluator.IsApplicable(s, element, model))
                .ToList();
            return Plan(element, applicable);
        }

        /// <summary>Plan from specifications already known to be applicable.</summary>
        public static ElementPlan Plan(ElementState element, IReadOnlyList<Specification> applicable)
        {
            var byKey = new Dictionary<string, PlannedRequirement>(StringComparer.Ordinal);
            var ordered = new List<PlannedRequirement>();
            var unplanned = new List<Facet>();
            var conflicts = new List<PlanConflict>();

            foreach (var specification in applicable)
            {
                foreach (var facet in specification.Requirements.Facets)
                {
                    if (!TryKey(facet, out string key, out string? pset, out string? name))
                    {
                        unplanned.Add(facet);
                        continue;
                    }

                    if (!byKey.TryGetValue(key, out var requirement))
                    {
                        requirement = new PlannedRequirement(facet.Kind, key, pset, name);
                        byKey[key] = requirement;
                        ordered.Add(requirement);
                    }

                    Merge(requirement, facet, specification, conflicts);
                }
            }

            foreach (var requirement in ordered)
            {
                ResolveValues(requirement, conflicts);
            }

            return new ElementPlan(element, applicable, ordered, conflicts, unplanned);
        }

        private static bool TryKey(Facet facet, out string key, out string? pset, out string? name)
        {
            pset = null;
            name = null;
            switch (facet)
            {
                case PropertyFacet property when property.PropertySet is SimpleValue p && property.BaseName is SimpleValue b:
                    pset = p.Text;
                    name = b.Text;
                    key = "property:" + p.Text + "/" + b.Text;
                    return true;
                case AttributeFacet attribute when attribute.Name is SimpleValue a:
                    name = a.Text;
                    key = "attribute:" + a.Text;
                    return true;
                case EntityFacet _:
                    key = "entity";
                    return true;
                case ClassificationFacet classification:
                    name = (classification.System as SimpleValue)?.Text;
                    key = "classification:" + (name ?? "*");
                    return classification.System == null || classification.System is SimpleValue;
                case MaterialFacet _:
                    key = "material";
                    return true;
                case PartOfFacet partOf:
                    name = (partOf.Entity.Name as SimpleValue)?.Text;
                    key = "partOf:" + (partOf.Relation ?? "*") + "/" + (name ?? "*");
                    return partOf.Entity.Name is SimpleValue;
                default:
                    key = "";
                    return false;
            }
        }

        private static void Merge(PlannedRequirement requirement, Facet facet, Specification specification,
            List<PlanConflict> conflicts)
        {
            requirement.Facets.Add(facet);
            if (!requirement.Sources.Contains(specification))
            {
                requirement.Sources.Add(specification);
            }

            var cardinality = facet is EntityFacet ? FacetCardinality.Required : facet.Cardinality;
            bool firstFacet = requirement.Facets.Count == 1;
            if (firstFacet)
            {
                requirement.Cardinality = cardinality;
            }
            else if ((requirement.Cardinality == FacetCardinality.Prohibited) != (cardinality == FacetCardinality.Prohibited))
            {
                conflicts.Add(new PlanConflict(requirement, ConflictReason.Cardinality,
                    requirement.Cardinality + " x " + cardinality));
            }
            else if (cardinality == FacetCardinality.Required)
            {
                requirement.Cardinality = FacetCardinality.Required;
            }

            switch (facet)
            {
                case PropertyFacet property:
                    if (property.DataType != null)
                    {
                        if (requirement.DataType == null)
                        {
                            requirement.DataType = property.DataType;
                        }
                        else if (!string.Equals(requirement.DataType, property.DataType, StringComparison.OrdinalIgnoreCase))
                        {
                            conflicts.Add(new PlanConflict(requirement, ConflictReason.DataType,
                                requirement.DataType + " x " + property.DataType));
                        }
                    }

                    AddValue(requirement, property.Value);
                    break;
                case AttributeFacet attribute:
                    AddValue(requirement, attribute.Value);
                    break;
                case EntityFacet entity:
                    AddValue(requirement, entity.Name);
                    if (entity.PredefinedType != null)
                    {
                        requirement.PredefinedTypes.Add(entity.PredefinedType);
                    }

                    break;
                case ClassificationFacet classification:
                    AddValue(requirement, classification.Value);
                    break;
                case MaterialFacet material:
                    AddValue(requirement, material.Value);
                    break;
            }
        }

        private static void AddValue(PlannedRequirement requirement, IdsValue? value)
        {
            if (value != null)
            {
                requirement.Values.Add(value);
            }
        }

        private static void ResolveValues(PlannedRequirement requirement, List<PlanConflict> conflicts)
        {
            if (requirement.Cardinality == FacetCardinality.Prohibited)
            {
                return;
            }

            string? dataType = requirement.Kind == FacetKind.Property ? requirement.DataType : null;
            requirement.AllowedValues = Intersect(requirement.Values, dataType);
            if (requirement.AllowedValues is { Count: 0 })
            {
                conflicts.Add(new PlanConflict(requirement,
                    requirement.Kind == FacetKind.Entity ? ConflictReason.Entity : ConflictReason.Value,
                    string.Join(" x ", requirement.Values.Select(Describe))));
            }

            requirement.AllowedPredefinedTypes = Intersect(requirement.PredefinedTypes, null);
            if (requirement.AllowedPredefinedTypes is { Count: 0 })
            {
                conflicts.Add(new PlanConflict(requirement, ConflictReason.Entity,
                    "predefinedType " + string.Join(" x ", requirement.PredefinedTypes.Select(Describe))));
            }
        }

        /// <summary>
        /// Values that meet every restriction, starting from the first closed list; <c>null</c> when none is closed (free value:
        /// a conflict cannot be proven).
        /// </summary>
        private static IReadOnlyList<string>? Intersect(IReadOnlyList<IdsValue> values, string? dataType)
        {
            List<string>? candidates = null;
            foreach (var value in values)
            {
                var closed = ClosedList(value);
                if (closed != null)
                {
                    candidates = closed.ToList();
                    break;
                }
            }

            return candidates?
                .Where(c => values.All(v => ValueMatcher.Matches(v, TypedValue.FromText(c, dataType))))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        private static IReadOnlyList<string>? ClosedList(IdsValue value) =>
            value switch
            {
                SimpleValue simple => new[] { simple.Text },
                RestrictionValue restriction => restriction.ClosedList(),
                _ => null,
            };

        internal static string Describe(IdsValue value) =>
            value switch
            {
                SimpleValue simple => "'" + simple.Text + "'",
                RestrictionValue restriction when restriction.ClosedList() is { } list => "{" + string.Join("|", list) + "}",
                RestrictionValue restriction => Tr.T("restrição") + (restriction.Patterns.Count > 0
                    ? " /" + string.Join("|", restriction.Patterns) + "/"
                    : ""),
                _ => "?",
            };
    }

    /// <summary>Converts the text of an IDS value into the typed value the model would have.</summary>
    public static class TypedValue
    {
        public static StateValue FromText(string text, string? dataType)
        {
            switch (IfcDataTypes.Classify(dataType))
            {
                case IfcValueKind.Integer:
                    return long.TryParse(text, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out long integer)
                        ? StateValue.Of(integer)
                        : StateValue.Of(text);
                case IfcValueKind.Real:
                    return ValueMatcher.TryParseNumber(text, out double real) ? StateValue.Of(real) : StateValue.Of(text);
                case IfcValueKind.Boolean:
                    return ValueMatcher.TryParseBoolean(text, out bool flag) ? StateValue.Of(flag) : StateValue.Of(text);
                default:
                    return StateValue.Of(text);
            }
        }
    }
}
