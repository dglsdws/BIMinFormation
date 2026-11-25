using System;
using System.Collections.Generic;
using System.Linq;
using BIMinFormation.Exporter;
using BIMinFormation.Ids;
using BIMinFormation.Rules;

namespace BIMinFormation.Core.Fill
{
    /// <summary>Status of a cell in the Fill In Parameters grid.</summary>
    public enum CellStatus
    {
        /// <summary>The model value meets the requirement.</summary>
        Ok,

        /// <summary>The exporter calculates the value (base quantities): nothing to fill in.</summary>
        Automatic,

        /// <summary>The user typed a valid value that has not been written yet.</summary>
        Changed,

        /// <summary>Required, without a value.</summary>
        Pending,

        /// <summary>Optional, without a value.</summary>
        Optional,

        /// <summary>The value (from the model or typed) does not meet the restriction.</summary>
        Invalid,

        /// <summary>Comes from modeling or can only be checked by hand (material, containment, attribute without a name).</summary>
        Manual,

        /// <summary>This exporter version does not write what the IDS asks for (goes to the report for the client).</summary>
        NotExportable,

        /// <summary>Prohibited requirement: the value must not exist.</summary>
        Prohibited,
    }

    public sealed class CellResult
    {
        public CellStatus Status { get; }
        public string? Message { get; }

        public CellResult(CellStatus status, string? message = null)
        {
            Status = status;
            Message = message;
        }

        public override string ToString() => Status + (Message != null ? ": " + Message : "");
    }

    /// <summary>Computes the status of a cell: model value × typed value × restriction × cardinality.</summary>
    public static class FillCell
    {
        /// <summary>Text shown when the selection has different values.</summary>
        public const string Mixed = "(misto)";

        /// <param name="valueType">Type of the parameter that receives the value, when the IDS gives no dataType (e.g. the
        /// Pset_*.* parameters installed by Autodesk, such as IFCBOOLEAN in Pset_WallCommon.LoadBearing).</param>
        /// <param name="choices">Values the exporter writes (enumerated standard property, e.g. Status = NEW, EXISTING...):
        /// outside them the exporter drops the property.</param>
        /// <param name="positive">The value must be greater than zero (positive measure in the IDS or in the standard Pset).</param>
        /// <param name="exporterCalculates">For a quantity: the exporter calculates it when it is not given (not the weights,
        /// see <see cref="ValueOrigins.NotCalculatedByExporter"/>, nor one missing in the last IFC check).</param>
        public static CellResult Evaluate(PlannedRequirement requirement, ValueOrigin origin, StateValue? modelValue,
            string? typedText, string? valueType = null, IReadOnlyList<string>? choices = null, bool positive = false,
            bool exporterCalculates = true)
        {
            string? dataType = requirement.DataType ?? valueType;
            switch (origin)
            {
                case ValueOrigin.ExporterQuantity:
                    // the exporter reads the Qto parameter when it has a value (checked in Revit 2027: Qto_CoveringBaseQuantities.Width
                    // of a roof, which it does not calculate); empty = calculated by the exporter
                    bool given = typedText == null ? modelValue != null && !modelValue.IsEmpty : typedText.Length > 0 && typedText != Mixed;
                    if (!given && exporterCalculates) return new CellResult(CellStatus.Automatic, Tr.T("calculado pelo exportador"));
                    break; // given, or not calculated: a value to fill in like any other
                case ValueOrigin.Modeling:
                    return new CellResult(CellStatus.Manual, Tr.T("conferir na modelagem"));
                case ValueOrigin.NotExportable:
                    return new CellResult(CellStatus.NotExportable, Tr.T("o exportador não escreve este valor como o IDS pede"));
            }

            bool typed = typedText != null && typedText != Mixed;
            StateValue? value = typed
                ? (typedText!.Length == 0 ? StateValue.Null : TypedValue.FromText(typedText, dataType))
                : modelValue;
            bool hasValue = value != null && !value.IsEmpty;

            if (requirement.Cardinality == FacetCardinality.Prohibited)
            {
                return hasValue && Satisfies(requirement, value!)
                    ? new CellResult(CellStatus.Prohibited, Tr.T("a ficha proíbe este valor"))
                    : new CellResult(typed ? CellStatus.Changed : CellStatus.Ok);
            }

            if (!hasValue)
            {
                return requirement.Cardinality == FacetCardinality.Optional
                    ? new CellResult(CellStatus.Optional)
                    : new CellResult(CellStatus.Pending, Tr.T("obrigatório"));
            }

            if (typed && !TypeMatches(dataType, value!))
            {
                return new CellResult(CellStatus.Invalid, Tr.T("valor não é ") + Describe(dataType));
            }

            if ((positive || IfcDataTypes.IsPositiveMeasure(dataType))
                && (value!.Kind == ValueKind.Real || value.Kind == ValueKind.Integer) && value.Number <= 0)
            {
                return new CellResult(CellStatus.Invalid, Tr.T("precisa ser maior que zero (medida positiva: o exportador não escreve zero)"));
            }

            if (!Satisfies(requirement, value!))
            {
                string allowed = requirement.AllowedValues != null ? Tr.T(" (aceita: ") + string.Join(", ", requirement.AllowedValues) + ")" : "";
                return new CellResult(CellStatus.Invalid, Tr.T("não atende à ficha") + allowed);
            }

            if (choices != null && value!.Kind == ValueKind.String && !choices.Contains(value.Text!, StringComparer.Ordinal))
            {
                return new CellResult(CellStatus.Invalid, Tr.T("o exportador só escreve um destes valores: ") + string.Join(", ", choices));
            }

            return new CellResult(typed && !SameAsModel(value!, modelValue) ? CellStatus.Changed : CellStatus.Ok);
        }

        /// <summary>Common value of a multiple selection, or <see cref="Mixed"/>.</summary>
        public static string? Common(IEnumerable<StateValue?> values)
        {
            var texts = values.Select(v => v == null || v.IsEmpty ? "" : v.ToString()).Distinct().ToList();
            return texts.Count == 1 ? texts[0] : Mixed;
        }

        private static bool Satisfies(PlannedRequirement requirement, StateValue value) =>
            requirement.Values.All(v => ValueMatcher.Matches(v, value));

        private static bool TypeMatches(string? dataType, StateValue value)
        {
            switch (IfcDataTypes.Classify(dataType))
            {
                case IfcValueKind.Integer:
                    return value.Kind == ValueKind.Integer;
                case IfcValueKind.Real:
                    return value.Kind == ValueKind.Real;
                case IfcValueKind.Boolean:
                    return value.Kind == ValueKind.Boolean;
                default:
                    return true;
            }
        }

        private static bool SameAsModel(StateValue value, StateValue? model) =>
            model != null && !model.IsEmpty && value.ToString() == model.ToString();

        private static string Describe(string? dataType)
        {
            switch (IfcDataTypes.Classify(dataType))
            {
                case IfcValueKind.Integer:
                    return Tr.T("um número inteiro");
                case IfcValueKind.Real:
                    return Tr.T("um número (use ponto decimal)");
                case IfcValueKind.Boolean:
                    return Tr.T("true ou false");
                default:
                    return Tr.T("texto");
            }
        }
    }
}
