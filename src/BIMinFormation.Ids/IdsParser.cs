using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace BIMinFormation.Ids
{
    /// <summary>Result of reading a file: the document (when readable) and the diagnostics.</summary>
    public sealed class IdsReadResult
    {
        public IdsDocument? Document { get; }

        public IReadOnlyList<IdsDiagnostic> Diagnostics { get; }

        public IdsReadResult(IdsDocument? document, IReadOnlyList<IdsDiagnostic> diagnostics)
        {
            Document = document;
            Diagnostics = diagnostics;
        }

        public bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
    }

    /// <summary>
    /// Reads IDS 1.0 files. Never throws for invalid content: everything becomes an <see cref="IdsDiagnostic"/>. The
    /// structure follows the official <c>ids.xsd</c>; the semantic rules live in <see cref="IdsAuditor"/>.
    /// </summary>
    public static class IdsParser
    {
        public const string IdsNamespace = "http://standards.buildingsmart.org/IDS";
        public const string XsNamespace = "http://www.w3.org/2001/XMLSchema";

        private static readonly XNamespace Ids = IdsNamespace;
        private static readonly XNamespace Xs = XsNamespace;

        public static IdsReadResult ReadFile(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            {
                return Read(stream, Path.GetFullPath(path));
            }
        }

        public static IdsReadResult ReadText(string xml, string? sourceName = null)
        {
            using (var reader = new StringReader(xml))
            {
                return Read(reader, sourceName);
            }
        }

        public static IdsReadResult Read(Stream stream, string? sourceName = null)
        {
            using (var reader = new StreamReader(stream))
            {
                return Read(reader, sourceName);
            }
        }

        private static IdsReadResult Read(TextReader reader, string? sourceName)
        {
            var context = new Context(sourceName);
            XDocument xml;

            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                using (XmlReader xmlReader = XmlReader.Create(reader, settings))
                {
                    xml = XDocument.Load(xmlReader, LoadOptions.SetLineInfo);
                }
            }
            catch (XmlException ex)
            {
                context.Error(DiagnosticCodes.XmlInvalid, Tr.T("XML inválido: ") + ex.Message, ex.LineNumber);
                return new IdsReadResult(null, context.Diagnostics);
            }

            XElement? root = xml.Root;
            if (root == null || root.Name != Ids + "ids")
            {
                context.Error(DiagnosticCodes.NotIds,
                    Tr.T("O elemento raiz não é <ids:ids> no namespace ") + IdsNamespace + ".", LineOf(root));
                return new IdsReadResult(null, context.Diagnostics);
            }

            IdsInfo info = ReadInfo(root.Element(Ids + "info"), context);
            var specifications = new List<Specification>();
            XElement? specsElement = root.Element(Ids + "specifications");

            if (specsElement == null)
            {
                context.Error(DiagnosticCodes.MissingElement, Tr.T("Falta <specifications>."), LineOf(root));
            }
            else
            {
                int index = 0;
                foreach (XElement child in specsElement.Elements())
                {
                    if (child.Name != Ids + "specification")
                    {
                        context.UnknownElement(child);
                        continue;
                    }

                    Specification? spec = ReadSpecification(child, index, context);
                    if (spec != null)
                    {
                        specifications.Add(spec);
                        index++;
                    }
                }

                if (specifications.Count == 0)
                {
                    context.Warning(DiagnosticCodes.NoSpecifications, Tr.T("Nenhuma especificação no arquivo."),
                        LineOf(specsElement));
                }
            }

            var document = new IdsDocument(info, specifications, sourceName);
            var diagnostics = new List<IdsDiagnostic>(context.Diagnostics);
            diagnostics.AddRange(IdsAuditor.Audit(document));
            return new IdsReadResult(document, diagnostics);
        }

        private static IdsInfo ReadInfo(XElement? element, Context context)
        {
            if (element == null)
            {
                context.Error(DiagnosticCodes.MissingElement, Tr.T("Falta <info>."), 0);
                return new IdsInfo("", null, null, null, null, null, null, null);
            }

            string? Text(string name) => element.Element(Ids + name)?.Value;

            string? title = Text("title");
            if (title == null)
            {
                context.Error(DiagnosticCodes.MissingElement, Tr.T("Falta <info>/<title>."), LineOf(element));
            }

            return new IdsInfo(title ?? "", Text("copyright"), Text("version"), Text("description"), Text("author"),
                Text("date"), Text("purpose"), Text("milestone"));
        }

        private static Specification? ReadSpecification(XElement element, int index, Context context)
        {
            string? name = (string?)element.Attribute("name");
            context.CurrentSpecification = name;
            int line = LineOf(element);

            if (name == null)
            {
                context.Error(DiagnosticCodes.MissingElement, Tr.T("Especificação sem o atributo name."), line);
                name = "";
            }

            var versions = new List<IfcSchemaVersion>();
            string? ifcVersion = (string?)element.Attribute("ifcVersion");
            if (string.IsNullOrWhiteSpace(ifcVersion))
            {
                context.Error(DiagnosticCodes.InvalidIfcVersion, Tr.T("Especificação sem ifcVersion."), line);
            }
            else
            {
                foreach (string token in ifcVersion!.Split(new[] { ' ', '\t', '\r', '\n' },
                             StringSplitOptions.RemoveEmptyEntries))
                {
                    if (TryParseSchema(token, out IfcSchemaVersion version))
                    {
                        if (!versions.Contains(version))
                        {
                            versions.Add(version);
                        }
                    }
                    else
                    {
                        context.Error(DiagnosticCodes.InvalidIfcVersion,
                            Tr.T("ifcVersion '") + token + Tr.T("' inválido (aceitos: IFC2X3, IFC4, IFC4X3_ADD2)."), line);
                    }
                }
            }

            XElement? applicabilityElement = element.Element(Ids + "applicability");
            Applicability applicability;
            if (applicabilityElement == null)
            {
                context.Error(DiagnosticCodes.MissingElement, Tr.T("Falta <applicability>."), line);
                applicability = new Applicability(new Facet[0], 1, 1);
            }
            else
            {
                applicability = ReadApplicability(applicabilityElement, context);
            }

            Requirements requirements = Requirements.Empty;
            XElement? requirementsElement = element.Element(Ids + "requirements");
            if (requirementsElement != null)
            {
                requirements = new Requirements(ReadFacets(requirementsElement, isRequirement: true, context),
                    (string?)requirementsElement.Attribute("description"));
            }

            foreach (XElement child in element.Elements())
            {
                if (child.Name != Ids + "applicability" && child.Name != Ids + "requirements")
                {
                    context.UnknownElement(child);
                }
            }

            var spec = new Specification(name, (string?)element.Attribute("identifier"),
                (string?)element.Attribute("description"), (string?)element.Attribute("instructions"), versions,
                applicability, requirements, context.SourceName, index, line);
            context.CurrentSpecification = null;
            return spec;
        }

        private static Applicability ReadApplicability(XElement element, Context context)
        {
            int min = ReadOccurs(element, "minOccurs", context) ?? 1;
            int? max = 1;
            XAttribute? maxAttribute = element.Attribute("maxOccurs");
            if (maxAttribute != null)
            {
                max = maxAttribute.Value.Trim() == "unbounded" ? null : ReadOccurs(element, "maxOccurs", context);
            }

            return new Applicability(ReadFacets(element, isRequirement: false, context), min, max);
        }

        private static int? ReadOccurs(XElement element, string attribute, Context context)
        {
            XAttribute? value = element.Attribute(attribute);
            if (value == null)
            {
                return null;
            }

            if (int.TryParse(value.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int result))
            {
                return result;
            }

            context.Error(DiagnosticCodes.InvalidOccurs,
                attribute + " '" + value.Value + Tr.T("' não é um inteiro não negativo nem 'unbounded'."), LineOf(element));
            return null;
        }

        private static IReadOnlyList<Facet> ReadFacets(XElement container, bool isRequirement, Context context)
        {
            var facets = new List<Facet>();
            foreach (XElement child in container.Elements())
            {
                Facet? facet = ReadFacet(child, isRequirement, context);
                if (facet != null)
                {
                    facets.Add(facet);
                }
            }

            return facets;
        }

        private static Facet? ReadFacet(XElement element, bool isRequirement, Context context)
        {
            if (element.Name.Namespace != Ids)
            {
                context.UnknownElement(element);
                return null;
            }

            int line = LineOf(element);
            string? instructions = (string?)element.Attribute("instructions");
            string? uri = (string?)element.Attribute("uri");

            switch (element.Name.LocalName)
            {
                case "entity":
                    return ReadEntity(element, context);

                case "attribute":
                {
                    FacetCardinality cardinality = ReadCardinality(element, isRequirement, allowOptional: true, context);
                    IdsValue name = RequiredValue(element, "name", context);
                    return new AttributeFacet(name, OptionalValue(element, "value", context), cardinality,
                        instructions, line);
                }

                case "property":
                {
                    FacetCardinality cardinality = ReadCardinality(element, isRequirement, allowOptional: true, context);
                    IdsValue propertySet = RequiredValue(element, "propertySet", context);
                    IdsValue baseName = RequiredValue(element, "baseName", context);
                    string? dataType = (string?)element.Attribute("dataType");
                    return new PropertyFacet(propertySet, baseName, OptionalValue(element, "value", context),
                        dataType?.Trim(), cardinality, instructions, uri, line);
                }

                case "classification":
                {
                    FacetCardinality cardinality = ReadCardinality(element, isRequirement, allowOptional: true, context);
                    return new ClassificationFacet(OptionalValue(element, "value", context),
                        OptionalValue(element, "system", context), cardinality, instructions, uri, line);
                }

                case "material":
                {
                    FacetCardinality cardinality = ReadCardinality(element, isRequirement, allowOptional: true, context);
                    return new MaterialFacet(OptionalValue(element, "value", context), cardinality, instructions, uri,
                        line);
                }

                case "partOf":
                {
                    FacetCardinality cardinality =
                        ReadCardinality(element, isRequirement, allowOptional: false, context);
                    XElement? entityElement = element.Element(Ids + "entity");
                    EntityFacet entity;
                    if (entityElement == null)
                    {
                        context.Error(DiagnosticCodes.MissingElement, Tr.T("partOf sem <entity>."), line);
                        entity = new EntityFacet(new SimpleValue("", line), null, null, line);
                    }
                    else
                    {
                        entity = ReadEntity(entityElement, context);
                    }

                    return new PartOfFacet(entity, ((string?)element.Attribute("relation"))?.Trim(), cardinality,
                        instructions, line);
                }

                default:
                    context.UnknownElement(element);
                    return null;
            }
        }

        private static EntityFacet ReadEntity(XElement element, Context context)
        {
            IdsValue name = RequiredValue(element, "name", context);
            return new EntityFacet(name, OptionalValue(element, "predefinedType", context),
                (string?)element.Attribute("instructions"), LineOf(element));
        }

        private static FacetCardinality ReadCardinality(XElement element, bool isRequirement, bool allowOptional,
            Context context)
        {
            XAttribute? attribute = element.Attribute("cardinality");
            if (attribute == null)
            {
                return FacetCardinality.Required;
            }

            if (!isRequirement)
            {
                context.Error(DiagnosticCodes.InvalidCardinality,
                    Tr.T("cardinality só é permitido em <requirements>."), LineOf(element));
                return FacetCardinality.Required;
            }

            switch (attribute.Value.Trim())
            {
                case "required":
                    return FacetCardinality.Required;
                case "prohibited":
                    return FacetCardinality.Prohibited;
                case "optional" when allowOptional:
                    return FacetCardinality.Optional;
                default:
                    context.Error(DiagnosticCodes.InvalidCardinality,
                        Tr.T("cardinality '") + attribute.Value + Tr.T("' inválido para <") + element.Name.LocalName + ">.",
                        LineOf(element));
                    return FacetCardinality.Required;
            }
        }

        private static IdsValue RequiredValue(XElement parent, string name, Context context)
        {
            IdsValue? value = OptionalValue(parent, name, context);
            if (value != null)
            {
                return value;
            }

            context.Error(DiagnosticCodes.MissingElement,
                "<" + parent.Name.LocalName + Tr.T("> sem <") + name + ">.", LineOf(parent));
            return new SimpleValue("", LineOf(parent));
        }

        private static IdsValue? OptionalValue(XElement parent, string name, Context context)
        {
            XElement? element = parent.Element(Ids + name);
            if (element == null)
            {
                return null;
            }

            XElement? simple = element.Element(Ids + "simpleValue");
            if (simple != null)
            {
                return new SimpleValue(simple.Value, LineOf(simple));
            }

            XElement? restriction = element.Element(Xs + "restriction");
            if (restriction != null)
            {
                return ReadRestriction(restriction);
            }

            context.Error(DiagnosticCodes.MissingElement,
                "<" + name + Tr.T("> sem <simpleValue> nem <xs:restriction>."), LineOf(element));
            return new SimpleValue("", LineOf(element));
        }

        private static RestrictionValue ReadRestriction(XElement element)
        {
            List<string> All(string facet) =>
                element.Elements(Xs + facet).Select(e => (string?)e.Attribute("value") ?? "").ToList();

            string? One(string facet) => (string?)element.Element(Xs + facet)?.Attribute("value");

            return new RestrictionValue(
                (string?)element.Attribute("base"),
                All("enumeration"),
                All("pattern"),
                One("minInclusive"),
                One("maxInclusive"),
                One("minExclusive"),
                One("maxExclusive"),
                One("length"),
                One("minLength"),
                One("maxLength"),
                One("totalDigits"),
                One("fractionDigits"),
                LineOf(element));
        }

        public static bool TryParseSchema(string token, out IfcSchemaVersion version)
        {
            switch (token)
            {
                case "IFC2X3":
                    version = IfcSchemaVersion.Ifc2X3;
                    return true;
                case "IFC4":
                    version = IfcSchemaVersion.Ifc4;
                    return true;
                case "IFC4X3_ADD2":
                    version = IfcSchemaVersion.Ifc4X3Add2;
                    return true;
                default:
                    version = default;
                    return false;
            }
        }

        private static int LineOf(XObject? node) => node is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : 0;

        private sealed class Context
        {
            public Context(string? sourceName)
            {
                SourceName = sourceName;
            }

            public string? SourceName { get; }

            public string? CurrentSpecification { get; set; }

            public List<IdsDiagnostic> Diagnostics { get; } = new List<IdsDiagnostic>();

            public void Error(string code, string message, int line) =>
                Diagnostics.Add(new IdsDiagnostic(DiagnosticSeverity.Error, code, message, SourceName, line,
                    CurrentSpecification));

            public void Warning(string code, string message, int line) =>
                Diagnostics.Add(new IdsDiagnostic(DiagnosticSeverity.Warning, code, message, SourceName, line,
                    CurrentSpecification));

            public void UnknownElement(XElement element) =>
                Error(DiagnosticCodes.UnknownElement, Tr.T("Elemento não previsto no IDS 1.0: <") + element.Name.LocalName + ">.",
                    LineOf(element));
        }
    }
}
