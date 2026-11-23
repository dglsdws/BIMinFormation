using System;
using System.Collections.Generic;
using System.Linq;
using BIMinFormation.Ids;

namespace BIMinFormation.Rules
{
    /// <summary>Where the facet is: in the applicability every facet counts as <c>required</c>.</summary>
    public enum FacetContext
    {
        Applicability,
        Requirement,
    }

    /// <summary>Evaluates an IDS 1.0 facet on an <see cref="ElementState"/>.</summary>
    public static class FacetEvaluator
    {
        public const string RelAggregates = "IFCRELAGGREGATES";
        public const string RelAssignsToGroup = "IFCRELASSIGNSTOGROUP";
        public const string RelContained = "IFCRELCONTAINEDINSPATIALSTRUCTURE";
        public const string RelNests = "IFCRELNESTS";
        public const string RelVoidsFills = "IFCRELVOIDSELEMENT IFCRELFILLSELEMENT";

        private const int MaxDepth = 256;

        public static FacetResult Evaluate(Facet facet, ElementState element, ModelState model, FacetContext context)
        {
            var cardinality = context == FacetContext.Applicability ? FacetCardinality.Required : facet.Cardinality;
            bool optional = cardinality == FacetCardinality.Optional;

            FacetResult result;
            switch (facet)
            {
                case EntityFacet entity:
                    return Entity(entity, element, model);
                case AttributeFacet attribute:
                    result = Attribute(attribute, element, optional);
                    break;
                case PropertyFacet property:
                    result = Property(property, element, optional);
                    break;
                case ClassificationFacet classification:
                    result = Classification(classification, element, optional);
                    break;
                case MaterialFacet material:
                    result = Material(material, element, optional);
                    break;
                case PartOfFacet partOf:
                    result = PartOf(partOf, element, model);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(facet), facet.GetType().Name);
            }

            if (cardinality == FacetCardinality.Prohibited)
            {
                return result.Passed ? FacetResult.Fail(FailureReason.Prohibited) : FacetResult.Pass;
            }

            return result;
        }

        // ---- entity ---------------------------------------------------------------------------

        private static FacetResult Entity(EntityFacet facet, ElementState element, ModelState model)
        {
            bool nameMatches = ValueMatcher.Matches(facet.Name, element.Entity);
            if (!nameMatches && model.Schema == IfcSchemaVersion.Ifc2X3 && element.TypeEntity != null)
            {
                // IFC2X3: the class comes from the type table (IfcFlowTerminal + IfcAirTerminalType = IfcAirTerminal)
                nameMatches = ValueMatcher.Matches(facet.Name, OccurrenceClassOf(element.TypeEntity));
            }

            if (!nameMatches)
            {
                return FacetResult.Fail(FailureReason.EntityMismatch, element.Entity);
            }

            return PredefinedType(facet, element);
        }

        private static FacetResult PredefinedType(EntityFacet facet, ElementState element)
        {
            if (facet.PredefinedType == null)
            {
                return FacetResult.Pass;
            }

            bool matches = facet.PredefinedType is SimpleValue simple && simple.Text == "USERDEFINED"
                ? element.IsUserDefined
                : ValueMatcher.Matches(facet.PredefinedType, element.PredefinedType);
            return matches ? FacetResult.Pass : FacetResult.Fail(FailureReason.PredefinedTypeMismatch, element.PredefinedType);
        }

        internal static string OccurrenceClassOf(string typeEntity)
        {
            if (typeEntity.EndsWith("TYPE", StringComparison.Ordinal))
            {
                return typeEntity.Substring(0, typeEntity.Length - 4);
            }

            return typeEntity.EndsWith("STYLE", StringComparison.Ordinal)
                ? typeEntity.Substring(0, typeEntity.Length - 5)
                : typeEntity;
        }

        // ---- attribute ------------------------------------------------------------------------

        private static FacetResult Attribute(AttributeFacet facet, ElementState element, bool optional)
        {
            var values = new List<StateValue>();
            if (facet.Name is SimpleValue simple)
            {
                if (element.Attributes.TryGetValue(simple.Text, out var value))
                {
                    values.Add(value);
                }
            }
            else
            {
                foreach (var pair in element.Attributes)
                {
                    if (ValueMatcher.Matches(facet.Name, pair.Key))
                    {
                        values.Add(pair.Value);
                    }
                }
            }

            if (values.Count == 0)
            {
                return optional ? FacetResult.Pass : FacetResult.Fail(FailureReason.AttributeMissing);
            }

            var filled = values.Where(v => !v.IsEmpty).ToList();
            if (filled.Count == 0)
            {
                // optional accepts a null attribute, but not an empty value ('', empty list, UNKNOWN)
                bool allNull = values.All(v => v.Kind == ValueKind.Null);
                return optional && allNull ? FacetResult.Pass : FacetResult.Fail(FailureReason.AttributeEmpty);
            }

            if (facet.Value != null)
            {
                foreach (var value in filled)
                {
                    if (!ValueMatcher.Matches(facet.Value, value))
                    {
                        return FacetResult.Fail(FailureReason.AttributeValue, value.ToString());
                    }
                }
            }

            return FacetResult.Pass;
        }

        // ---- property -------------------------------------------------------------------------

        private static FacetResult Property(PropertyFacet facet, ElementState element, bool optional)
        {
            var psets = element.PropertySets.Where(p => ValueMatcher.Matches(facet.PropertySet, p.Name)).ToList();
            if (psets.Count == 0)
            {
                return optional ? FacetResult.Pass : FacetResult.Fail(FailureReason.PropertySetMissing);
            }

            foreach (var pset in psets)
            {
                var properties = pset.Properties
                    .Where(p => ValueMatcher.Matches(facet.BaseName, p.Name) && p.HasValue)
                    .ToList();
                if (properties.Count == 0)
                {
                    if (optional) continue;
                    return FacetResult.Fail(FailureReason.PropertyMissing, pset.Name);
                }

                foreach (var property in properties)
                {
                    var result = PropertyValue(facet, property);
                    if (!result.Passed)
                    {
                        return result;
                    }
                }
            }

            return FacetResult.Pass;
        }

        private static FacetResult PropertyValue(PropertyFacet facet, PropertyState property)
        {
            IReadOnlyList<StateValue> candidates;
            switch (property.Kind)
            {
                case PropertyKind.Reference:
                case PropertyKind.Complex:
                case PropertyKind.Other:
                    return FacetResult.Fail(FailureReason.PropertyUnsupported, property.Kind.ToString());

                case PropertyKind.Table:
                {
                    var columns = facet.DataType == null
                        ? property.Columns
                        : property.Columns.Where(c => SameType(c.DataType, facet.DataType)).ToList();
                    if (columns.Count == 0)
                    {
                        return FacetResult.Fail(FailureReason.PropertyDataType,
                            string.Join(", ", property.Columns.Select(c => c.DataType)));
                    }

                    candidates = columns.SelectMany(c => c.Values).ToList();
                    break;
                }

                case PropertyKind.Predefined:
                    candidates = property.Values;
                    break;

                default:
                    if (facet.DataType != null && !SameType(property.DataType, facet.DataType))
                    {
                        return FacetResult.Fail(FailureReason.PropertyDataType, property.DataType);
                    }

                    candidates = property.Values;
                    break;
            }

            if (facet.Value == null)
            {
                return FacetResult.Pass;
            }

            var filled = candidates.Where(v => !v.IsEmpty).ToList();
            bool any = property.Kind == PropertyKind.Single || property.Kind == PropertyKind.Quantity
                       || property.Kind == PropertyKind.Predefined
                ? filled.Count > 0 && filled.All(v => ValueMatcher.Matches(facet.Value, v))
                : filled.Any(v => ValueMatcher.Matches(facet.Value, v));
            return any
                ? FacetResult.Pass
                : FacetResult.Fail(FailureReason.PropertyValue, string.Join(", ", filled.Select(v => v.ToString())));
        }

        private static bool SameType(string? actual, string expected) =>
            actual != null && string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

        // ---- classification -------------------------------------------------------------------

        private static FacetResult Classification(ClassificationFacet facet, ElementState element, bool optional)
        {
            if (element.Classifications.Count == 0)
            {
                return optional ? FacetResult.Pass : FacetResult.Fail(FailureReason.ClassificationMissing);
            }

            bool systemSeen = false;
            foreach (var reference in element.Classifications)
            {
                bool systemMatches = facet.System == null || ValueMatcher.Matches(facet.System, reference.System);
                if (!systemMatches) continue;
                systemSeen = true;
                if (facet.Value == null || reference.Values.Any(v => ValueMatcher.Matches(facet.Value, v)))
                {
                    return FacetResult.Pass;
                }
            }

            string actual = string.Join(", ", element.Classifications.Select(c =>
                (c.System ?? "?") + (c.Values.Count > 0 ? ":" + string.Join("/", c.Values) : "")));
            return FacetResult.Fail(
                systemSeen ? FailureReason.ClassificationValue : FailureReason.ClassificationSystem, actual);
        }

        // ---- material -------------------------------------------------------------------------

        private static FacetResult Material(MaterialFacet facet, ElementState element, bool optional)
        {
            if (element.Material == null)
            {
                return optional ? FacetResult.Pass : FacetResult.Fail(FailureReason.MaterialMissing);
            }

            if (facet.Value == null || element.Material.Values.Any(v => ValueMatcher.Matches(facet.Value, v)))
            {
                return FacetResult.Pass;
            }

            return FacetResult.Fail(FailureReason.MaterialValue, string.Join(", ", element.Material.Values));
        }

        // ---- partOf ---------------------------------------------------------------------------

        private static FacetResult PartOf(PartOfFacet facet, ElementState element, ModelState model)
        {
            switch (facet.Relation)
            {
                case null:
                    return Chain(facet, element, e => AnyParent(e, model));
                case RelAggregates:
                    return Chain(facet, element, e => model.Find(e.Relations.Aggregate));
                case RelNests:
                    return Chain(facet, element, e => model.Find(e.Relations.Nest));
                case RelAssignsToGroup:
                    return AnyOf(facet, element.Relations.Groups.Select(model.Find).Where(g => g != null).ToList()!);
                case RelContained:
                {
                    var container = Container(element, model);
                    return AnyOf(facet, container == null ? new List<ElementState>() : new List<ElementState> { container });
                }

                case RelVoidsFills:
                {
                    ElementState? host = element.Relations.Voids != null
                        ? model.Find(element.Relations.Voids)
                        : model.Find(model.Find(element.Relations.FillsVoid)?.Relations.Voids);
                    return AnyOf(facet, host == null ? new List<ElementState>() : new List<ElementState> { host });
                }

                default:
                    return FacetResult.Fail(FailureReason.RelationMissing, facet.Relation);
            }
        }

        /// <summary>Climbs the parent chain until one matches the facet entity.</summary>
        private static FacetResult Chain(PartOfFacet facet, ElementState element, Func<ElementState, ElementState?> parentOf)
        {
            var parent = parentOf(element);
            if (parent == null)
            {
                return FacetResult.Fail(FailureReason.RelationMissing);
            }

            var seen = new List<string>();
            FacetResult? last = null;
            for (int depth = 0; parent != null && depth < MaxDepth; depth++)
            {
                var result = MatchesParent(facet.Entity, parent);
                if (result.Passed)
                {
                    return result;
                }

                seen.Add(parent.Entity);
                last = result;
                parent = parentOf(parent);
            }

            return FacetResult.Fail(last?.Reason ?? FailureReason.RelationEntity, string.Join(" > ", seen));
        }

        private static FacetResult AnyOf(PartOfFacet facet, IReadOnlyList<ElementState> candidates)
        {
            if (candidates.Count == 0)
            {
                return FacetResult.Fail(FailureReason.RelationMissing);
            }

            FacetResult? last = null;
            foreach (var candidate in candidates)
            {
                last = MatchesParent(facet.Entity, candidate);
                if (last.Passed)
                {
                    return last;
                }
            }

            return last!;
        }

        private static FacetResult MatchesParent(EntityFacet entity, ElementState parent)
        {
            if (!ValueMatcher.Matches(entity.Name, parent.Entity))
            {
                return FacetResult.Fail(FailureReason.RelationEntity, parent.Entity);
            }

            var predefined = PredefinedType(entity, parent);
            return predefined.Passed
                ? predefined
                : FacetResult.Fail(FailureReason.RelationPredefinedType, parent.PredefinedType);
        }

        /// <summary>Parent in the spatial tree: direct container, aggregate, nest, opening or voided element.</summary>
        private static ElementState? SpatialParent(ElementState element, ModelState model) =>
            model.Find(element.Relations.Container)
            ?? model.Find(element.Relations.Aggregate)
            ?? model.Find(element.Relations.Nest)
            ?? model.Find(element.Relations.FillsVoid)
            ?? model.Find(element.Relations.Voids);

        /// <summary>Any parent (spatial tree and, last, the first group).</summary>
        private static ElementState? AnyParent(ElementState element, ModelState model) =>
            SpatialParent(element, model)
            ?? element.Relations.Groups.Select(model.Find).FirstOrDefault(g => g != null);

        /// <summary>Direct spatial container or, if there is none, the parent's (indirect containment).</summary>
        private static ElementState? Container(ElementState element, ModelState model)
        {
            var current = element;
            for (int depth = 0; current != null && depth < MaxDepth; depth++)
            {
                var direct = model.Find(current.Relations.Container);
                if (direct != null)
                {
                    return direct;
                }

                current = SpatialParent(current, model);
            }

            return null;
        }
    }
}
