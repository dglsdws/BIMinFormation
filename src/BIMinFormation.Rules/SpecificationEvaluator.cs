using System.Collections.Generic;
using BIMinFormation.Ids;

namespace BIMinFormation.Rules
{
    /// <summary>
    /// Evaluates IDS 1.0 specifications. An element is applicable when it meets every applicability facet; then it must
    /// meet every requirement. The specification passes when:
    /// <list type="bullet">
    /// <item><c>required</c>: there are at least <c>minOccurs</c> applicable elements and all of them pass (a numeric
    /// <c>maxOccurs</c> does not limit the count);</item>
    /// <item><c>optional</c>: every applicable element passes (zero applicable elements also passes);</item>
    /// <item><c>prohibited</c>: there is no applicable element (the requirements are ignored).</item>
    /// </list>
    /// </summary>
    public static class SpecificationEvaluator
    {
        public static SpecificationResult Evaluate(Specification specification, ModelState model)
        {
            var applicable = new List<ElementResult>();
            foreach (var element in model.Elements)
            {
                var result = EvaluateElement(specification, element, model);
                if (result.IsApplicable)
                {
                    applicable.Add(result);
                }
            }

            return new SpecificationResult(specification, applicable, IsPassed(specification, applicable));
        }

        public static IReadOnlyList<SpecificationResult> Evaluate(IEnumerable<Specification> specifications, ModelState model)
        {
            var results = new List<SpecificationResult>();
            foreach (var specification in specifications)
            {
                results.Add(Evaluate(specification, model));
            }

            return results;
        }

        /// <summary>A specification on a single element (used by the plugin: pre-validation per element).</summary>
        public static ElementResult EvaluateElement(Specification specification, ElementState element, ModelState model)
        {
            if (!IsApplicable(specification, element, model))
            {
                return new ElementResult(element, false, new FacetFailure[0]);
            }

            var failures = new List<FacetFailure>();
            if (specification.Usage != SpecificationUsage.Prohibited)
            {
                foreach (var facet in specification.Requirements.Facets)
                {
                    var result = FacetEvaluator.Evaluate(facet, element, model, FacetContext.Requirement);
                    if (!result.Passed)
                    {
                        failures.Add(new FacetFailure(facet, result));
                    }
                }
            }

            return new ElementResult(element, true, failures);
        }

        public static bool IsApplicable(Specification specification, ElementState element, ModelState model)
        {
            var facets = specification.Applicability.Facets;
            if (facets.Count == 0)
            {
                return false;
            }

            foreach (var facet in facets)
            {
                if (!FacetEvaluator.Evaluate(facet, element, model, FacetContext.Applicability).Passed)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsPassed(Specification specification, IReadOnlyList<ElementResult> applicable)
        {
            var applicability = specification.Applicability;
            if (specification.Usage == SpecificationUsage.Prohibited)
            {
                return applicable.Count == 0;
            }

            foreach (var result in applicable)
            {
                if (!result.Passed)
                {
                    return false;
                }
            }

            // maxOccurs only distinguishes "prohibited" (0); a numeric bound is not applied, as in IfcTester: the XSD default
            // (1) would make any specification with two elements fail.
            return applicable.Count >= applicability.MinOccurs;
        }
    }
}
