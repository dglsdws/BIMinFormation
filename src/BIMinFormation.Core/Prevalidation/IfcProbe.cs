using System;
using System.Collections.Generic;
using System.Linq;
using BIMinFormation.Ids;
using BIMinFormation.Rules;

namespace BIMinFormation.Core.Prevalidation
{
    /// <summary>Summary of the check in the IFC.</summary>
    public sealed class IfcProbeInfo
    {
        /// <summary>IFC exported for the check (temporary folder).</summary>
        public string IfcPath { get; }

        public DateTime When { get; }
        public IfcSchemaVersion Schema { get; }

        /// <summary>Checks (specification × element) decided by the IFC.</summary>
        public int Decided { get; set; }

        /// <summary>Of the decided ones, how many were "to check" in the model.</summary>
        public int Resolved { get; set; }

        /// <summary>Model elements with a specification that were not exported to the IFC (category not exported, for example).</summary>
        public int NotExported { get; set; }

        /// <summary>Exported to the IFC, but there the specification does not apply (different entity or predefined type).</summary>
        public int NotApplicableInIfc { get; set; }

        /// <summary>Applicable only in the IFC, with no matching Revit element (e.g. project, site).</summary>
        public int OnlyInIfc { get; set; }

        public IfcProbeInfo(string ifcPath, DateTime when, IfcSchemaVersion schema)
        {
            IfcPath = ifcPath;
            When = when;
            Schema = schema;
        }
    }

    /// <summary>
    /// Check in the IFC: the specifications evaluated on the actually exported IFC, linked to the Revit elements by
    /// GlobalId. What was "to check" in the model (quantities, material, Psets the exporter calculates…) becomes pass or
    /// fail, because the IFC is what will be delivered.
    /// </summary>
    public static class IfcProbe
    {
        /// <summary>
        /// One per IFC file and without the GlobalId of an element in the model (Revit gives them project-level GUIDs): linked
        /// by entity when the IFC has exactly one.
        /// </summary>
        private static readonly HashSet<string> ProjectLevel = new HashSet<string>(StringComparer.Ordinal) { "IFCPROJECT", "IFCBUILDING", "IFCSITE" };

        public static PrevalidationResult Merge(PrevalidationResult model, ModelState ifc, string ifcPath, DateTime when)
        {
            var info = new IfcProbeInfo(ifcPath, when, ifc.Schema);
            var specifications = model.Specifications.Select(s => s.Specification).ToList();

            // the IFC evaluated as the client's validator would, by GlobalId
            var exported = new Dictionary<string, ElementState>(StringComparer.Ordinal);
            foreach (var element in ifc.Elements)
            {
                string? id = element.GlobalId;
                if (id != null && !exported.ContainsKey(id)) exported[id] = element;
            }

            // the project-level entity of the IFC, when there is only one of it
            var single = ifc.Elements.Where(e => ProjectLevel.Contains(e.Entity) && e.GlobalId != null).GroupBy(e => e.Entity)
                .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single().GlobalId!, StringComparer.Ordinal);
            string? Key(ElementState element) =>
                ProjectLevel.Contains(element.Entity) && single.TryGetValue(element.Entity, out var id) ? id : element.GlobalId;

            var inIfc = specifications.ToDictionary(s => s, s => new Dictionary<string, ElementResult>(StringComparer.Ordinal));
            var index = new SpecificationIndex(specifications);
            foreach (var element in ifc.Elements)
            {
                string? id = element.GlobalId;
                if (id == null) continue;
                foreach (var specification in index.Applicable(element, ifc))
                {
                    inIfc[specification][id] = SpecificationEvaluator.EvaluateElement(specification, element, ifc);
                }
            }

            var revitByGlobalId = new Dictionary<string, ElementState>(StringComparer.Ordinal);
            foreach (var element in model.Model.Elements)
            {
                string? id = Key(element);
                if (id != null && !revitByGlobalId.ContainsKey(id)) revitByGlobalId[id] = element;
            }

            var checks = new List<SpecificationCheck>();
            foreach (var check in model.Specifications)
            {
                var results = inIfc[check.Specification];
                var elements = new List<ElementCheck>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var element in check.Elements)
                {
                    string? id = Key(element.Element);
                    if (id != null) seen.Add(id);
                    if (id != null && results.TryGetValue(id, out var result))
                    {
                        elements.Add(FromIfc(check.Specification, element.Element, result, exported.TryGetValue(id, out var inFile) ? inFile : null, ifc.Schema));
                        info.Decided++;
                        if (element.Status == CheckStatus.Unknown) info.Resolved++;
                    }
                    else if (id != null && exported.TryGetValue(id, out var other))
                    {
                        info.NotApplicableInIfc++;
                        elements.Add(Keep(check.Specification, element,
                            Tr.T("no IFC a ficha não alcança o elemento (saiu como ") + other.Entity + ")"));
                    }
                    else
                    {
                        info.NotExported++;
                        elements.Add(Keep(check.Specification, element, Tr.T("não saiu no IFC")));
                    }
                }

                // applicable in the IFC but not put in the specification by the model
                foreach (var pair in results.Where(p => !seen.Contains(p.Key)))
                {
                    if (revitByGlobalId.TryGetValue(pair.Key, out var revit))
                    {
                        elements.Add(FromIfc(check.Specification, revit, pair.Value, exported.TryGetValue(pair.Key, out var inFile) ? inFile : null, ifc.Schema));
                        info.Decided++;
                    }
                    else
                    {
                        info.OnlyInIfc++;
                    }
                }

                checks.Add(new SpecificationCheck(check.Specification, elements));
            }

            return new PrevalidationResult(checks, model.Orphans, model.Model, info);
        }

        private static ElementCheck FromIfc(Specification specification, ElementState revit, ElementResult result, ElementState? inFile,
            IfcSchemaVersion schema) =>
            new ElementCheck(revit, specification.Requirements.Facets.Select(f =>
            {
                var failure = result.Failures.FirstOrDefault(x => ReferenceEquals(x.Facet, f));
                string label = Prevalidator.Label(f);
                return failure == null
                    ? new RequirementCheck(f, CheckStatus.Pass, label, Quantity(f, inFile, schema))
                    : new RequirementCheck(f, CheckStatus.Fail, label, Tr.T("no IFC: ") + Prevalidator.Describe(failure.Result));
            }).ToList(), fromIfc: true);

        /// <summary>The value of a quantity in the IFC (the exporter calculates it only when exporting): "in the IFC: 12.5".</summary>
        private static string? Quantity(Facet facet, ElementState? inFile, IfcSchemaVersion schema)
        {
            if (inFile == null || !(facet is PropertyFacet { PropertySet: SimpleValue set, BaseName: SimpleValue name })
                || !(set.Text.StartsWith("Qto_", StringComparison.Ordinal) || set.Text == "BaseQuantities"))
            {
                return null;
            }

            var value = inFile.PropertySets.FirstOrDefault(s => s.Name == set.Text)?.Properties.FirstOrDefault(p => p.Name == name.Text)
                ?.Values.FirstOrDefault(v => !v.IsEmpty);
            return value == null ? null : Tr.T("no IFC: ") + QuantityText(value, IfcSchemaCatalog.For(schema).PropertySet(set.Text)?.Property(name.Text)?.DataType ?? ((PropertyFacet)facet).DataType);
        }

        /// <summary>
        /// A quantity as the user reads it: 12 significant digits (no noise from the feet conversion) and the weight in kg. The
        /// reader keeps the IfcTester rule for quantities (mass converted to grams, unlike the properties), so the engine and the
        /// IfcTester agree; only the text shows kg.
        /// </summary>
        public static string QuantityText(StateValue value, string? dataType)
        {
            if (value.Kind != ValueKind.Real && value.Kind != ValueKind.Integer) return value.ToString();
            bool mass = string.Equals(dataType, "IFCMASSMEASURE", StringComparison.OrdinalIgnoreCase);
            double number = mass ? value.Number / 1000 : value.Number;
            return double.Parse(number.ToString("G12", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture)
                       .ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                   + (mass ? " kg" : "");
        }

        /// <summary>
        /// The IFC does not decide: the element stays to check, with the reason (not exported, or the specification does not
        /// apply there). Model failures stay failures.
        /// </summary>
        private static ElementCheck Keep(Specification specification, ElementCheck element, string note)
        {
            var requirements = element.Requirements.Select(r => r.Status == CheckStatus.Unknown
                ? new RequirementCheck(r.Facet, r.Status, r.Label, (r.Reason != null ? r.Reason + "; " : "") + note)
                : r).ToList();
            if (!requirements.Any(r => r.Status == CheckStatus.Unknown))
            {
                requirements.Insert(0, new RequirementCheck(specification.Applicability.Facets.First(), CheckStatus.Unknown, "IFC", note));
            }

            return new ElementCheck(element.Element, requirements);
        }
    }
}
