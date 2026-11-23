namespace BIMinFormation.Ids
{
    public enum DiagnosticSeverity
    {
        /// <summary>The file does not follow IDS 1.0; a validator would not accept it, or the rule never passes.</summary>
        Error,

        /// <summary>The file is accepted, but something should be reviewed by the author or the user.</summary>
        Warning,
    }

    /// <summary>Stable codes, used in tests and in the report.</summary>
    public static class DiagnosticCodes
    {
        public const string XmlInvalid = "IDS001";
        public const string NotIds = "IDS002";
        public const string MissingElement = "IDS003";
        public const string InvalidIfcVersion = "IDS004";
        public const string InvalidOccurs = "IDS005";
        public const string ProhibitedWithRequirements = "IDS006";
        public const string InvalidCardinality = "IDS007";
        public const string EntityNotUppercase = "IDS008";
        public const string ImpossibleEntityRequirement = "IDS009";
        public const string InvalidDataType = "IDS010";
        public const string ValueNotValidForDataType = "IDS011";
        public const string PatternOnNonString = "IDS012";
        public const string InvalidRegex = "IDS013";
        public const string InvalidRelation = "IDS014";
        public const string UnknownElement = "IDS015";
        public const string UnknownEntity = "IDS016";
        public const string InvalidAttribute = "IDS017";
        public const string AttributeValueNotComparable = "IDS018";
        public const string PredefinedTypeNotSupported = "IDS019";

        public const string EmptyName = "IDSW01";
        public const string UnusualOccurs = "IDSW02";
        public const string UnknownDataType = "IDSW03";
        public const string MissingDataType = "IDSW04";
        public const string EmptyApplicability = "IDSW05";
        public const string DuplicateSpecification = "IDSW06";
        public const string NoSpecifications = "IDSW07";
        public const string EntityNotInAllVersions = "IDSW08";
        public const string PropertyNotInStandardSet = "IDSW09";
        public const string UnknownStandardSet = "IDSW10";
        public const string StandardDataTypeMismatch = "IDSW11";
        public const string StandardValueNotValid = "IDSW12";
    }

    public sealed class IdsDiagnostic
    {
        public DiagnosticSeverity Severity { get; }

        public string Code { get; }

        public string Message { get; }

        public string? FilePath { get; }

        public int Line { get; }

        public string? SpecificationName { get; }

        public IdsDiagnostic(DiagnosticSeverity severity, string code, string message, string? filePath, int line,
            string? specificationName)
        {
            Severity = severity;
            Code = code;
            Message = message;
            FilePath = filePath;
            Line = line;
            SpecificationName = specificationName;
        }

        public override string ToString()
        {
            string where = FilePath == null ? "" : System.IO.Path.GetFileName(FilePath) + (Line > 0 ? ":" + Line : "") + " ";
            string spec = SpecificationName == null ? "" : "[" + SpecificationName + "] ";
            return where + Code + " " + spec + Message;
        }
    }
}
