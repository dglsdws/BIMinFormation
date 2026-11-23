using System.Collections.Generic;
using BIMinFormation.Ids;

namespace BIMinFormation.Rules
{
    public enum FailureReason
    {
        EntityMismatch,
        PredefinedTypeMismatch,
        AttributeMissing,
        AttributeEmpty,
        AttributeValue,
        PropertySetMissing,
        PropertyMissing,
        PropertyDataType,
        PropertyUnsupported,
        PropertyValue,
        ClassificationMissing,
        ClassificationValue,
        ClassificationSystem,
        MaterialMissing,
        MaterialValue,
        RelationMissing,
        RelationEntity,
        RelationPredefinedType,
        /// <summary>A <c>prohibited</c> facet that was satisfied.</summary>
        Prohibited,
    }

    /// <summary>Result of a facet for an element.</summary>
    public sealed class FacetResult
    {
        public bool Passed { get; }
        public FailureReason? Reason { get; }

        /// <summary>What the element has (for the message), when it makes sense.</summary>
        public string? Actual { get; }

        private FacetResult(bool passed, FailureReason? reason, string? actual)
        {
            Passed = passed;
            Reason = reason;
            Actual = actual;
        }

        public static FacetResult Pass { get; } = new FacetResult(true, null, null);

        public static FacetResult Fail(FailureReason reason, string? actual = null) =>
            new FacetResult(false, reason, actual);

        public override string ToString() => Passed ? "ok" : Reason + (Actual != null ? " (" + Actual + ")" : "");
    }

    public sealed class FacetFailure
    {
        public Facet Facet { get; }
        public FacetResult Result { get; }

        public FacetFailure(Facet facet, FacetResult result)
        {
            Facet = facet;
            Result = result;
        }

        public override string ToString() => Facet.Kind + ": " + Result;
    }

    /// <summary>Result of a specification for an element.</summary>
    public sealed class ElementResult
    {
        public ElementState Element { get; }

        /// <summary>The element meets the applicability.</summary>
        public bool IsApplicable { get; }

        /// <summary>Requirements not met (empty when the element passes or is not applicable).</summary>
        public IReadOnlyList<FacetFailure> Failures { get; }

        public ElementResult(ElementState element, bool isApplicable, IReadOnlyList<FacetFailure> failures)
        {
            Element = element;
            IsApplicable = isApplicable;
            Failures = failures;
        }

        public bool Passed => IsApplicable && Failures.Count == 0;
    }

    /// <summary>Result of a specification on a whole model.</summary>
    public sealed class SpecificationResult
    {
        public Specification Specification { get; }

        /// <summary>Applicable elements, with the failures of each.</summary>
        public IReadOnlyList<ElementResult> Applicable { get; }

        public bool Passed { get; }

        public SpecificationResult(Specification specification, IReadOnlyList<ElementResult> applicable, bool passed)
        {
            Specification = specification;
            Applicable = applicable;
            Passed = passed;
        }

        public int FailedCount
        {
            get
            {
                int count = 0;
                foreach (var result in Applicable)
                {
                    if (!result.Passed) count++;
                }

                return count;
            }
        }

        public override string ToString() =>
            Specification.Name + ": " + (Passed ? Tr.T("passa") : Tr.T("falha")) + " (" + Applicable.Count + Tr.T(" aplicáveis, ")
            + FailedCount + Tr.T(" com falha)");
    }
}
