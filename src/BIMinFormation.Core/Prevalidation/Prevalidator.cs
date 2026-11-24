using System.Collections.Generic;
using System.Linq;
using BIMinFormation.Core.Classification;
using BIMinFormation.Exporter;
using BIMinFormation.Ids;
using BIMinFormation.Rules;

namespace BIMinFormation.Core.Prevalidation
{
    /// <summary>Status of a requirement (or of an element) in the pre-validation.</summary>
    public enum CheckStatus
    {
        /// <summary>Met, and the IFC will be exported that way.</summary>
        Pass,

        /// <summary>Not met, and the IFC will be exported that way.</summary>
        Fail,

        /// <summary>Depends on the exporter or on modeling: only the IFC confirms.</summary>
        Unknown,
    }

    /// <summary>A specification requirement for an element.</summary>
    public sealed class RequirementCheck
    {
        public Facet Facet { get; }
        public CheckStatus Status { get; }

        /// <summary>What the requirement is (e.g. <c>BIMPR.Material</c>, <c>entity</c>).</summary>
        public string Label { get; }

        /// <summary>Why it failed or why it stays to check; <c>null</c> when it passes.</summary>
        public string? Reason { get; }

        /// <summary>Quantity calculated by the exporter (Qto): nothing to do in the model.</summary>
        public bool ByExporter { get; }

        public RequirementCheck(Facet facet, CheckStatus status, string label, string? reason, bool byExporter = false)
        {
            Facet = facet;
            Status = status;
            Label = label;
            Reason = reason;
            ByExporter = byExporter;
        }

        public override string ToString() => Label + (Reason != null ? ": " + Reason : "");
    }

    /// <summary>An element in a specification: the status of each requirement and of the element (fail &gt; to check &gt; pass).</summary>
    public sealed class ElementCheck
    {
        public ElementState Element { get; }
        public IReadOnlyList<RequirementCheck> Requirements { get; }

        public CheckStatus Status => Requirements.Any(r => r.Status == CheckStatus.Fail) ? CheckStatus.Fail
            : Requirements.Any(r => r.Status == CheckStatus.Unknown) ? CheckStatus.Unknown
            : CheckStatus.Pass;

        /// <summary>
        /// Everything the model decides is right and only quantities are missing, which the exporter calculates. It stays "to
        /// check" for the contract with the IFC (the exporter sometimes does not write a quantity), but there is nothing to do in the model.
        /// </summary>
        public bool OkInModel => Status == CheckStatus.Unknown
                                 && Requirements.Where(r => r.Status == CheckStatus.Unknown).All(r => r.ByExporter);

        /// <summary>Status taken from the IFC exported by the check in the IFC, not only from the model.</summary>
        public bool FromIfc { get; }

        public ElementCheck(ElementState element, IReadOnlyList<RequirementCheck> requirements, bool fromIfc = false)
        {
            Element = element;
            Requirements = requirements;
            FromIfc = fromIfc;
        }
    }

    public sealed class SpecificationCheck
    {
        public Specification Specification { get; }
        public IReadOnlyList<ElementCheck> Elements { get; }

        public int Passed => Elements.Count(e => e.Status == CheckStatus.Pass);
        public int Failed => Elements.Count(e => e.Status == CheckStatus.Fail);
        public int Unknown => Elements.Count(e => e.Status == CheckStatus.Unknown);

        /// <summary>Pass or are "ok in the model" (only exporter quantities missing).</summary>
        public int Ok => Elements.Count(e => e.Status == CheckStatus.Pass || e.OkInModel);

        /// <summary>Really to check: material, empty standard Pset, classification… (not only quantities).</summary>
        public int ToCheck => Elements.Count(e => e.Status == CheckStatus.Unknown && !e.OkInModel);

        /// <summary>
        /// Required specification without any applicable element (minOccurs ≥ 1): the IDS fails it, but it is a warning, not
        /// an element failure (BIMPR marks every specification as required; each project uses the ones in its BEP).
        /// </summary>
        public bool MissingRequired => Elements.Count == 0 && Specification.Usage == SpecificationUsage.Required;

        public SpecificationCheck(Specification specification, IReadOnlyList<ElementCheck> elements)
        {
            Specification = specification;
            Elements = elements;
        }
    }

    public sealed class PrevalidationResult
    {
        public IReadOnlyList<SpecificationCheck> Specifications { get; }

        /// <summary>Elements with an identity (e.g. EOI) that matches no specification.</summary>
        public IReadOnlyList<RecognitionResult> Orphans { get; }

        /// <summary>The evaluated model (every element read, applicable or not).</summary>
        public ModelState Model { get; }

        /// <summary>Check in the exported IFC; <c>null</c> when the result comes only from the model.</summary>
        public IfcProbeInfo? Probe { get; }

        public PrevalidationResult(IReadOnlyList<SpecificationCheck> specifications, IReadOnlyList<RecognitionResult> orphans, ModelState model,
            IfcProbeInfo? probe = null)
        {
            Specifications = specifications;
            Orphans = orphans;
            Model = model;
            Probe = probe;
        }
    }

    /// <summary>
    /// Pre-validation: the specifications evaluated on the model read from Revit, before exporting. The engine is the same
    /// used to validate the IFC; the difference is what the model does not tell by itself. By kind of requirement:
    /// <list type="bullet">
    /// <item>entity, predefined type, plugin parameters, links, filled Psets and overridden attributes: pass or fail,
    /// because the exporter writes what is in the model;</item>
    /// <item>quantities, material, "part of", missing classification, empty standard Pset (the exporter sometimes
    /// calculates it, e.g. <c>IsExternal</c> from the wall function), attributes the exporter fills by itself: to check.</item>
    /// </list>
    /// Contract (tested against IfcTester): "pass" in the plugin passes in the IFC; "fail" in the plugin fails in the IFC.
    /// </summary>
    public static class Prevalidator
    {
        /// <summary>Attributes the model reader knows as the exporter writes them.</summary>
        private static readonly HashSet<string> KnownAttributes = new HashSet<string> { "GlobalId" };

        public static PrevalidationResult Run(IReadOnlyList<Specification> specifications, ModelState model, IfcSchemaCatalog schema,
            ExporterCatalog exporter)
        {
            // index: each element is tested only against the specifications that may apply to it (same result as testing one by one)
            var index = new SpecificationIndex(specifications);
            var bySpecification = specifications.ToDictionary(s => s, s => new List<ElementCheck>());
            foreach (var element in model.Elements)
            {
                foreach (var specification in index.Applicable(element, model))
                {
                    var result = SpecificationEvaluator.EvaluateElement(specification, element, model);
                    bySpecification[specification].Add(new ElementCheck(element, specification.Requirements.Facets
                        .Select(f => ZeroInPositive(f, element, schema)
                                     ?? Check(f, result.Failures.FirstOrDefault(x => ReferenceEquals(x.Facet, f))?.Result, schema, exporter))
                        .ToList()));
                }
            }

            var checks = specifications.Select(s => new SpecificationCheck(s, bySpecification[s])).ToList();

            var recognizer = new Recognizer(specifications);
            var orphans = model.Elements.Select(e => recognizer.Recognize(e, model))
                .Where(r => r.Status == RecognitionStatus.OrphanIdentity)
                .ToList();
            return new PrevalidationResult(checks, orphans, model);
        }

        /// <summary>
        /// Zero (or less) in a positive measure (IDS dataType or standard Pset template): the schema requires more than zero
        /// and the Revit exporter does not write the property, so it fails in the IFC even when the model meets the IDS.
        /// </summary>
        private static RequirementCheck? ZeroInPositive(Facet facet, ElementState element, IfcSchemaCatalog schema)
        {
            if (!(facet is PropertyFacet property) || !(property.PropertySet is SimpleValue set) || !(property.BaseName is SimpleValue name))
                return null;
            if (!IfcDataTypes.IsPositiveMeasure(property.DataType)
                && !IfcDataTypes.IsPositiveMeasure(schema.PropertySet(set.Text)?.Property(name.Text)?.DataType))
                return null;
            var value = element.PropertySets.FirstOrDefault(p => p.Name == set.Text)?.Properties.FirstOrDefault(p => p.Name == name.Text)
                ?.Values.FirstOrDefault(v => v.Kind == ValueKind.Real || v.Kind == ValueKind.Integer);
            return value != null && value.Number <= 0
                ? new RequirementCheck(facet, CheckStatus.Fail, Label(facet), Tr.T("valor ") + value + Tr.T(": medida positiva, o exportador não escreve zero"))
                : null;
        }

        /// <summary>Status of a requirement from the engine result (<c>null</c> = met in the model).</summary>
        public static RequirementCheck Check(Facet facet, FacetResult? failure, IfcSchemaCatalog schema, ExporterCatalog exporter)
        {
            var origin = ValueOrigins.Classify(facet, schema, exporter);
            string label = Label(facet);
            if (origin.Origin == ValueOrigin.ExporterQuantity)
            {
                // given in the Qto parameter: the exporter writes that value (it reads the parameter before calculating)
                if (failure == null) return new RequirementCheck(facet, CheckStatus.Pass, label, null);

                return new RequirementCheck(facet, CheckStatus.Unknown, label, Tr.T("calculado pelo exportador"), byExporter: true);
            }

            if (failure == null)
            {
                return origin.Origin == ValueOrigin.NotExportable
                    ? new RequirementCheck(facet, CheckStatus.Unknown, label, Tr.T("o exportador não escreve como o .ids pede: ") + origin.Detail)
                    : new RequirementCheck(facet, CheckStatus.Pass, label, null);
            }

            var reason = failure.Reason;
            string text = Describe(failure);
            if (facet is AttributeFacet { Name: SimpleValue { Text: "" } } && facet.Cardinality == FacetCardinality.Required)
            {
                // defect of the .ids (e.g. Luxembourg): without the name no attribute matches, in any model
                return new RequirementCheck(facet, CheckStatus.Fail, label, Tr.T("defeito do .ids: atributo sem nome, nenhum modelo atende")
                    + (string.IsNullOrWhiteSpace(facet.Instructions) ? "" : Tr.T(" (instrução: ") + Short(facet.Instructions!) + ")"));
            }

            switch (facet)
            {
                case MaterialFacet _:
                    return new RequirementCheck(facet, CheckStatus.Unknown, label, Tr.T("conferir na modelagem (") + text + ")");
                case PartOfFacet _:
                    return new RequirementCheck(facet, CheckStatus.Unknown, label, Tr.T("conferir na modelagem (") + text + ")");
                case ClassificationFacet _ when reason != FailureReason.Prohibited:
                    // the exporter also writes classifications from other parameters (e.g. assembly code)
                    return new RequirementCheck(facet, CheckStatus.Unknown, label, Tr.T("conferir no IFC (") + text + ")");
                case AttributeFacet attribute when reason != FailureReason.Prohibited
                                                   && (reason == FailureReason.AttributeMissing || reason == FailureReason.AttributeEmpty)
                                                   && !(attribute.Name is SimpleValue name && KnownAttributes.Contains(name.Text)):
                    return new RequirementCheck(facet, CheckStatus.Unknown, label, Tr.T("o exportador pode preencher (") + text + ")");
                case PropertyFacet _ when origin.Origin == ValueOrigin.StandardParameter
                                          && (reason == FailureReason.PropertySetMissing || reason == FailureReason.PropertyMissing):
                    return new RequirementCheck(facet, CheckStatus.Unknown, label, Tr.T("vazio no modelo; o exportador pode calcular"));
            }

            return new RequirementCheck(facet, CheckStatus.Fail, label, text);
        }

        public static string Label(Facet facet)
        {
            switch (facet)
            {
                case EntityFacet _:
                    return Tr.T("entidade");
                case AttributeFacet attribute:
                    return Tr.T("atributo ") + Text(attribute.Name);
                case PropertyFacet property:
                    return Text(property.PropertySet) + "." + Text(property.BaseName);
                case ClassificationFacet classification:
                    return Tr.T("classificação") + (classification.System != null ? " " + Text(classification.System) : "");
                case MaterialFacet _:
                    return "material";
                case PartOfFacet partOf:
                    return Tr.T("faz parte de ") + Text(partOf.Entity.Name);
                default:
                    return facet.Kind.ToString();
            }
        }

        private static string Text(IdsValue? value) => value switch
        {
            SimpleValue simple => simple.Text.Length == 0 ? Tr.T("(sem nome)") : simple.Text,
            null => "",
            _ => Tr.T("(restrição)"),
        };

        private static string Short(string text)
        {
            string first = text.Split(new[] { "||" }, System.StringSplitOptions.None)[0].Trim();
            return first.Length > 60 ? first.Substring(0, 57) + "..." : first;
        }

        public static string Describe(FacetResult result)
        {
            string actual = result.Actual != null ? ": " + result.Actual : "";
            switch (result.Reason)
            {
                case FailureReason.EntityMismatch: return Tr.T("entidade diferente") + actual;
                case FailureReason.PredefinedTypeMismatch: return Tr.T("tipo predefinido diferente") + actual;
                case FailureReason.AttributeMissing: return Tr.T("atributo ausente");
                case FailureReason.AttributeEmpty: return Tr.T("atributo vazio");
                case FailureReason.AttributeValue: return Tr.T("valor fora do pedido") + actual;
                case FailureReason.PropertySetMissing: return Tr.T("conjunto ausente");
                case FailureReason.PropertyMissing: return Tr.T("propriedade ausente");
                case FailureReason.PropertyDataType: return Tr.T("tipo de dado diferente") + actual;
                case FailureReason.PropertyUnsupported: return Tr.T("propriedade não comparável") + actual;
                case FailureReason.PropertyValue: return Tr.T("valor fora do pedido") + actual;
                case FailureReason.ClassificationMissing: return Tr.T("sem classificação");
                case FailureReason.ClassificationValue: return Tr.T("código fora do pedido") + actual;
                case FailureReason.ClassificationSystem: return Tr.T("sistema fora do pedido") + actual;
                case FailureReason.MaterialMissing: return Tr.T("sem material");
                case FailureReason.MaterialValue: return Tr.T("material fora do pedido") + actual;
                case FailureReason.RelationMissing: return Tr.T("relação ausente");
                case FailureReason.RelationEntity: return Tr.T("relação com outra entidade") + actual;
                case FailureReason.RelationPredefinedType: return Tr.T("relação com outro tipo predefinido") + actual;
                case FailureReason.Prohibited: return Tr.T("a ficha proíbe e o elemento tem") + actual;
                default: return result.ToString();
            }
        }
    }
}
