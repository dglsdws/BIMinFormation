using System.Collections.Generic;

namespace BIMinFormation.Ids
{
    public enum IfcSchemaVersion
    {
        Ifc2X3,
        Ifc4,
        Ifc4X3Add2,
    }

    /// <summary>
    /// Usage of the specification, derived from the applicability <c>minOccurs</c>/<c>maxOccurs</c> (same rule as
    /// IfcTester): <c>maxOccurs = 0</c> prohibits; <c>minOccurs ≥ 1</c> requires at least one element; anything else is
    /// optional.
    /// </summary>
    public enum SpecificationUsage
    {
        Required,
        Optional,
        Prohibited,
    }

    public sealed class Applicability
    {
        public IReadOnlyList<Facet> Facets { get; }

        /// <summary>XSD default: 1.</summary>
        public int MinOccurs { get; }

        /// <summary><c>null</c> = <c>unbounded</c>. XSD default: 1.</summary>
        public int? MaxOccurs { get; }

        public Applicability(IReadOnlyList<Facet> facets, int minOccurs, int? maxOccurs)
        {
            Facets = facets;
            MinOccurs = minOccurs;
            MaxOccurs = maxOccurs;
        }

        public SpecificationUsage Usage =>
            MaxOccurs == 0 ? SpecificationUsage.Prohibited
            : MinOccurs >= 1 ? SpecificationUsage.Required
            : SpecificationUsage.Optional;
    }

    public sealed class Requirements
    {
        public IReadOnlyList<Facet> Facets { get; }

        public string? Description { get; }

        public Requirements(IReadOnlyList<Facet> facets, string? description)
        {
            Facets = facets;
            Description = description;
        }

        public static Requirements Empty { get; } = new Requirements(new Facet[0], null);
    }

    /// <summary>An IDS specification.</summary>
    public sealed class Specification
    {
        public string Name { get; }

        public string? Identifier { get; }

        public string? Description { get; }

        public string? Instructions { get; }

        public IReadOnlyList<IfcSchemaVersion> IfcVersions { get; }

        public Applicability Applicability { get; }

        public Requirements Requirements { get; }

        /// <summary>Source file (full path), or <c>null</c> if read from a stream.</summary>
        public string? SourceFile { get; }

        /// <summary>Position of the specification in the file (0, 1, 2…).</summary>
        public int Index { get; }

        public int Line { get; }

        public Specification(string name, string? identifier, string? description, string? instructions,
            IReadOnlyList<IfcSchemaVersion> ifcVersions, Applicability applicability, Requirements requirements,
            string? sourceFile, int index, int line)
        {
            Name = name;
            Identifier = identifier;
            Description = description;
            Instructions = instructions;
            IfcVersions = ifcVersions;
            Applicability = applicability;
            Requirements = requirements;
            SourceFile = sourceFile;
            Index = index;
            Line = line;
        }

        public SpecificationUsage Usage => Applicability.Usage;

        public override string ToString() => Name;
    }

    public sealed class IdsInfo
    {
        public string Title { get; }
        public string? Copyright { get; }
        public string? Version { get; }
        public string? Description { get; }
        public string? Author { get; }
        public string? Date { get; }
        public string? Purpose { get; }
        public string? Milestone { get; }

        public IdsInfo(string title, string? copyright, string? version, string? description, string? author,
            string? date, string? purpose, string? milestone)
        {
            Title = title;
            Copyright = copyright;
            Version = version;
            Description = description;
            Author = author;
            Date = date;
            Purpose = purpose;
            Milestone = milestone;
        }
    }

    /// <summary>A loaded <c>.ids</c> file.</summary>
    public sealed class IdsDocument
    {
        public IdsInfo Info { get; }

        public IReadOnlyList<Specification> Specifications { get; }

        public string? FilePath { get; }

        public IdsDocument(IdsInfo info, IReadOnlyList<Specification> specifications, string? filePath)
        {
            Info = info;
            Specifications = specifications;
            FilePath = filePath;
        }
    }
}
