using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace BIMinFormation.Ids
{
    /// <summary>
    /// IDS 1.0 semantic rules. The ones that depend on the IFC schema (entities, attributes, predefined types and standard
    /// Psets) use the <see cref="IfcSchemaCatalog"/> of each declared <c>ifcVersion</c>.
    /// </summary>
    public static class IdsAuditor
    {
        private static readonly HashSet<string> Relations = new HashSet<string>(StringComparer.Ordinal)
        {
            "IFCRELAGGREGATES", "IFCRELASSIGNSTOGROUP", "IFCRELCONTAINEDINSPATIALSTRUCTURE", "IFCRELNESTS",
            "IFCRELVOIDSELEMENT IFCRELFILLSELEMENT",
        };

        private static readonly Regex UpperCaseName = new Regex("^[A-Z]+$", RegexOptions.CultureInvariant);

        public static IReadOnlyList<IdsDiagnostic> Audit(IdsDocument document)
        {
            var diagnostics = new List<IdsDiagnostic>();
            foreach (Specification spec in document.Specifications)
            {
                new SpecAudit(spec, document.FilePath, diagnostics).Run();
            }

            return diagnostics;
        }

        private sealed class SpecAudit
        {
            private readonly Specification _spec;
            private readonly string? _file;
            private readonly List<IdsDiagnostic> _out;
            private readonly IReadOnlyList<IfcSchemaCatalog> _schemas;

            public SpecAudit(Specification spec, string? file, List<IdsDiagnostic> output)
            {
                _spec = spec;
                _file = file;
                _out = output;
                _schemas = spec.IfcVersions.Distinct().Select(IfcSchemaCatalog.For).ToList();
            }

            public void Run()
            {
                CheckOccurs();

                if (_spec.Applicability.Facets.Count == 0)
                {
                    Warning(DiagnosticCodes.EmptyApplicability,
                        Tr.T("applicability sem facetas: a especificação não se aplica a nenhum elemento."), _spec.Line);
                }

                foreach (Facet facet in _spec.Applicability.Facets)
                {
                    CheckFacet(facet);
                }

                foreach (Facet facet in _spec.Requirements.Facets)
                {
                    CheckFacet(facet);
                }

                CheckEntityRequirementIsPossible();

                foreach (Facet facet in _spec.Applicability.Facets.Concat(_spec.Requirements.Facets))
                {
                    CheckAgainstSchemas(facet);
                }
            }

            // ---- rules that depend on the IFC schema ---------------------------------------------

            private void CheckAgainstSchemas(Facet facet)
            {
                if (_schemas.Count == 0)
                {
                    return;
                }

                switch (facet)
                {
                    case EntityFacet entity:
                        CheckEntityInSchemas(entity);
                        break;
                    case PartOfFacet partOf:
                        CheckEntityInSchemas(partOf.Entity);
                        break;
                    case AttributeFacet attribute:
                        CheckAttributeInSchemas(attribute);
                        break;
                    case PropertyFacet property:
                        CheckStandardProperty(property);
                        break;
                }
            }

            private static IReadOnlyList<string> Names(IdsValue? value) =>
                value switch
                {
                    SimpleValue simple => new[] { simple.Text },
                    RestrictionValue restriction when restriction.Patterns.Count == 0 => restriction.Enumerations,
                    _ => new string[0],
                };

            /// <summary>In IFC2X3 the class also comes from the type (IfcAirTerminalType → IFCAIRTERMINAL).</summary>
            private static bool Exists(IfcSchemaCatalog schema, string entity) =>
                schema.HasEntity(entity)
                || (schema.Version == IfcSchemaVersion.Ifc2X3
                    && (schema.HasEntity(entity + "TYPE") || schema.HasEntity(entity + "STYLE")));

            private void CheckEntityInSchemas(EntityFacet facet)
            {
                foreach (string name in Names(facet.Name).Where(n => n.Length > 0))
                {
                    var found = _schemas.Where(s => Exists(s, name)).ToList();
                    if (found.Count == 0)
                    {
                        Error(DiagnosticCodes.UnknownEntity,
                            Tr.T("Entidade '") + name + Tr.T("' não existe em ") + Versions(_schemas) + ".", facet.Line);
                        continue;
                    }

                    if (found.Count < _schemas.Count)
                    {
                        Warning(DiagnosticCodes.EntityNotInAllVersions,
                            Tr.T("Entidade '") + name + Tr.T("' não existe em ") + Versions(_schemas.Except(found)) + ".", facet.Line);
                    }

                    if (facet.PredefinedType != null)
                    {
                        var unsupported = found.Where(s => !s.SupportsPredefinedType(name)).ToList();
                        if (unsupported.Count > 0)
                        {
                            Error(DiagnosticCodes.PredefinedTypeNotSupported,
                                Tr.T("Entidade '") + name + Tr.T("' não tem tipo predefinido em ") + Versions(unsupported) + ".",
                                facet.Line);
                        }
                    }
                }
            }

            private void CheckAttributeInSchemas(AttributeFacet facet)
            {
                var entities = _spec.Applicability.Facets.OfType<EntityFacet>().SelectMany(e => Names(e.Name)).ToList();
                if (entities.Count == 0 || facet.Name is SimpleValue { Text: "" })
                {
                    return;
                }

                foreach (string entity in entities)
                {
                    foreach (var schema in _schemas.Where(s => s.HasEntity(entity)))
                    {
                        var attributes = ResolveAttributes(schema, entity, facet.Name, out string? problem);
                        if (problem != null)
                        {
                            Error(DiagnosticCodes.InvalidAttribute, problem + " (" + schema.Name + ").", facet.Line);
                            return;
                        }

                        if (facet.Value == null)
                        {
                            continue;
                        }

                        foreach (var attribute in attributes)
                        {
                            if (!CheckAttributeValue(attribute, facet.Value, schema, facet.Line))
                            {
                                return;
                            }
                        }
                    }
                }
            }

            private static IReadOnlyList<IfcAttributeInfo> ResolveAttributes(IfcSchemaCatalog schema, string entity,
                IdsValue name, out string? problem)
            {
                problem = null;
                if (name is SimpleValue simple)
                {
                    switch (schema.LookupAttribute(entity, simple.Text))
                    {
                        case AttributeLookup.Direct:
                            return new[] { schema.Attribute(entity, simple.Text)! };
                        case AttributeLookup.Derived:
                            problem = Tr.T("Atributo '") + simple.Text + Tr.T("' de ") + entity + Tr.T(" é derivado e não pode ser verificado");
                            return new IfcAttributeInfo[0];
                        case AttributeLookup.Inverse:
                            problem = Tr.T("Atributo '") + simple.Text + Tr.T("' de ") + entity + Tr.T(" é inverso e não pode ser verificado");
                            return new IfcAttributeInfo[0];
                        default:
                            problem = Tr.T("Atributo '") + simple.Text + Tr.T("' não existe em ") + entity;
                            return new IfcAttributeInfo[0];
                    }
                }

                var matches = schema.AllAttributes(entity).Where(a => Accepts(name, a.Name)).ToList();
                if (matches.Count == 0)
                {
                    problem = Tr.T("Nenhum atributo de ") + entity + Tr.T(" atende à restrição do nome");
                }

                return matches;
            }

            /// <summary>Checks the required value against the nature of the attribute; returns false if it reported an error.</summary>
            private bool CheckAttributeValue(IfcAttributeInfo attribute, IdsValue value, IfcSchemaCatalog schema, int line)
            {
                switch (attribute.Kind)
                {
                    case AttributeValueKind.Entity:
                    case AttributeValueKind.Aggregate:
                    case AttributeValueKind.Select:
                    case AttributeValueKind.Binary:
                    case AttributeValueKind.Unknown:
                        Error(DiagnosticCodes.AttributeValueNotComparable,
                            Tr.T("Atributo '") + attribute.Name + "' (" + (attribute.DeclaredType ?? attribute.Kind.ToString())
                            + Tr.T(") não tem valor simples: só dá para exigir que exista (") + schema.Name + ").", line);
                        return false;
                }

                IfcValueKind kind = attribute.Kind switch
                {
                    AttributeValueKind.Integer => IfcValueKind.Integer,
                    AttributeValueKind.Real => IfcValueKind.Real,
                    AttributeValueKind.Number => IfcValueKind.Real,
                    AttributeValueKind.Boolean => IfcValueKind.Boolean,
                    AttributeValueKind.Logical => IfcValueKind.Boolean,
                    _ => IfcValueKind.String,
                };
                int before = _out.Count;
                CheckValueForKind(value, kind, attribute.DeclaredType ?? attribute.Name, line);
                return _out.Count == before;
            }

            /// <summary>Standard Pset/Qto: does the name exist? does the property exist in it? does the type match?</summary>
            private void CheckStandardProperty(PropertyFacet facet)
            {
                if (!(facet.PropertySet is SimpleValue pset) || !(facet.BaseName is SimpleValue property))
                {
                    return;
                }

                if (!pset.Text.StartsWith("Pset_", StringComparison.Ordinal) && !pset.Text.StartsWith("Qto_", StringComparison.Ordinal))
                {
                    return;
                }

                var templates = _schemas.Select(s => s.PropertySet(pset.Text)).Where(t => t != null).ToList();
                if (templates.Count == 0)
                {
                    Warning(DiagnosticCodes.UnknownStandardSet,
                        "'" + pset.Text + Tr.T("' usa o prefixo dos conjuntos padrão, mas não existe em ") + Versions(_schemas)
                        + Tr.T(". O exportador do Revit trata como conjunto do usuário."), facet.Line);
                    return;
                }

                var properties = templates.Select(t => t!.Property(property.Text)).Where(p => p != null).ToList();
                if (properties.Count == 0)
                {
                    Warning(DiagnosticCodes.PropertyNotInStandardSet,
                        "'" + property.Text + Tr.T("' não faz parte de ") + pset.Text + Tr.T(" em ") + Versions(_schemas)
                        + Tr.T(". O exportador do Revit descarta propriedades fora do conjunto padrão."), facet.Line);
                    return;
                }

                if (facet.DataType != null && properties.All(p => p!.DataType != null && p.DataType != facet.DataType))
                {
                    Warning(DiagnosticCodes.StandardDataTypeMismatch,
                        pset.Text + "." + property.Text + Tr.T(" é ")
                        + string.Join("/", properties.Select(p => p!.DataType).Distinct())
                        + Tr.T(" no padrão, mas o IDS pede ") + facet.DataType + ".", facet.Line);
                }

                // without a dataType the value is compared with the standard type: 'True' is not an IDS boolean (NL)
                var kinds = properties.Select(p => IfcDataTypes.Classify(p!.DataType)).Distinct().ToList();
                if (facet.DataType == null && kinds.Count == 1 && kinds[0] != IfcValueKind.String && kinds[0] != IfcValueKind.Unknown)
                {
                    var values = facet.Value switch
                    {
                        SimpleValue simple => new List<string> { simple.Text },
                        RestrictionValue restriction => restriction.Enumerations.ToList(),
                        _ => new List<string>(),
                    };
                    foreach (string invalid in values.Where(v => !IfcDataTypes.IsLexicallyValid(v, kinds[0])))
                    {
                        Warning(DiagnosticCodes.StandardValueNotValid,
                            Tr.T("Valor '") + invalid + Tr.T("' inválido para ") + pset.Text + "." + property.Text + " ("
                            + properties[0]!.DataType + ")" + Hint(kinds[0]) + Tr.T(": pela norma não casa com nenhum elemento.")
                            + (kinds[0] == IfcValueKind.Boolean ? Tr.T(" O IfcTester aceita e lê qualquer texto diferente de 'false' como verdadeiro.") : ""),
                            facet.Line);
                    }
                }
            }

            private static string Versions(IEnumerable<IfcSchemaCatalog> schemas) =>
                string.Join(", ", schemas.Select(s => s.Name));

            private void CheckOccurs()
            {
                Applicability a = _spec.Applicability;

                if (a.MaxOccurs.HasValue && a.MaxOccurs.Value < a.MinOccurs)
                {
                    Error(DiagnosticCodes.InvalidOccurs, Tr.T("maxOccurs menor que minOccurs."), _spec.Line);
                    return;
                }

                bool standard = (a.MinOccurs == 1 && a.MaxOccurs == null) ||
                                (a.MinOccurs == 0 && a.MaxOccurs == null) ||
                                (a.MinOccurs == 0 && a.MaxOccurs == 0);
                if (!standard)
                {
                    Warning(DiagnosticCodes.UnusualOccurs,
                        Tr.T("minOccurs/maxOccurs fora das combinações do IDS 1.0 (1..unbounded, 0..unbounded, 0..0); ") +
                        Tr.T("tratado como ") + DescribeUsage(a.Usage) + ".", _spec.Line);
                }

                if (a.Usage == SpecificationUsage.Prohibited && _spec.Requirements.Facets.Count > 0)
                {
                    Error(DiagnosticCodes.ProhibitedWithRequirements,
                        Tr.T("Especificação proibida (maxOccurs=0) não pode ter requirements."), _spec.Line);
                }
            }

            private void CheckFacet(Facet facet)
            {
                switch (facet)
                {
                    case EntityFacet entity:
                        CheckEntity(entity);
                        break;

                    case PartOfFacet partOf:
                        CheckEntity(partOf.Entity);
                        if (partOf.Relation != null && !Relations.Contains(partOf.Relation))
                        {
                            Error(DiagnosticCodes.InvalidRelation, Tr.T("relation '") + partOf.Relation + Tr.T("' inválida."),
                                partOf.Line);
                        }

                        break;

                    case AttributeFacet attribute:
                        CheckRegexes(attribute.Name);
                        CheckRegexes(attribute.Value);
                        if (attribute.Name is SimpleValue { Text: "" })
                        {
                            Warning(DiagnosticCodes.EmptyName,
                                Tr.T("attribute com nome vazio: a exigência só pode ser entendida pelo texto de ") +
                                "instructions" + Describe(attribute.Instructions) + ".", attribute.Line);
                        }

                        break;

                    case PropertyFacet property:
                        CheckProperty(property);
                        break;

                    case ClassificationFacet classification:
                        CheckRegexes(classification.Value);
                        CheckRegexes(classification.System);
                        break;

                    case MaterialFacet material:
                        CheckRegexes(material.Value);
                        break;
                }
            }

            private void CheckEntity(EntityFacet entity)
            {
                CheckRegexes(entity.Name);
                CheckRegexes(entity.PredefinedType);

                IEnumerable<string> names = entity.Name switch
                {
                    SimpleValue simple => new[] { simple.Text },
                    RestrictionValue restriction => restriction.Enumerations,
                    _ => Enumerable.Empty<string>(),
                };

                foreach (string name in names)
                {
                    if (name.Length == 0)
                    {
                        Error(DiagnosticCodes.MissingElement, Tr.T("Entidade com nome vazio."), entity.Line);
                    }
                    else if (!UpperCaseName.IsMatch(name.Replace("_", "")) || name != name.ToUpperInvariant())
                    {
                        Error(DiagnosticCodes.EntityNotUppercase,
                            Tr.T("Entidade '") + name + Tr.T("' deve ser escrita em maiúsculas (ex.: IFCWALL)."), entity.Line);
                    }
                }
            }

            private void CheckProperty(PropertyFacet property)
            {
                CheckRegexes(property.PropertySet);
                CheckRegexes(property.BaseName);
                CheckRegexes(property.Value);

                if (property.PropertySet is SimpleValue { Text: "" } || property.BaseName is SimpleValue { Text: "" })
                {
                    Warning(DiagnosticCodes.EmptyName, Tr.T("property com propertySet ou baseName vazio."), property.Line);
                }

                if (property.DataType == null)
                {
                    if (property.Value != null)
                    {
                        Warning(DiagnosticCodes.MissingDataType,
                            Tr.T("property com valor e sem dataType: a comparação tipada fica indefinida."), property.Line);
                    }

                    return;
                }

                if (!UpperCaseName.IsMatch(property.DataType))
                {
                    Error(DiagnosticCodes.InvalidDataType,
                        Tr.T("dataType '") + property.DataType + Tr.T("' inválido (só letras maiúsculas, ex.: IFCLABEL)."),
                        property.Line);
                    return;
                }

                IfcValueKind kind = IfcDataTypes.Classify(property.DataType);
                if (kind == IfcValueKind.Unknown)
                {
                    Warning(DiagnosticCodes.UnknownDataType,
                        Tr.T("dataType '") + property.DataType + Tr.T("' não reconhecido nesta versão do plugin."), property.Line);
                    return;
                }

                CheckValueForKind(property.Value, kind, property.DataType, property.Line);
            }

            private void CheckValueForKind(IdsValue? value, IfcValueKind kind, string dataType, int line)
            {
                switch (value)
                {
                    case SimpleValue simple:
                        if (!IfcDataTypes.IsLexicallyValid(simple.Text, kind))
                        {
                            Error(DiagnosticCodes.ValueNotValidForDataType,
                                Tr.T("Valor '") + simple.Text + Tr.T("' inválido para ") + dataType + Hint(kind) + ".", line);
                        }

                        break;

                    case RestrictionValue restriction:
                        if (restriction.Patterns.Count > 0 && kind != IfcValueKind.String)
                        {
                            Error(DiagnosticCodes.PatternOnNonString,
                                Tr.T("xs:pattern só se aplica a texto; ") + dataType + Tr.T(" não é texto."), line);
                        }

                        foreach (string item in restriction.Enumerations)
                        {
                            if (!IfcDataTypes.IsLexicallyValid(item, kind))
                            {
                                Error(DiagnosticCodes.ValueNotValidForDataType,
                                    Tr.T("Valor de enumeração '") + item + Tr.T("' inválido para ") + dataType + Hint(kind) + ".",
                                    line);
                            }
                        }

                        break;
                }
            }

            /// <summary>
            /// If the applicability fixes the entity and the entity requirement accepts none of them, no element can pass (official
            /// "invalid-entity" cases).
            /// </summary>
            private void CheckEntityRequirementIsPossible()
            {
                EntityFacet? applicable = _spec.Applicability.Facets.OfType<EntityFacet>().FirstOrDefault();
                EntityFacet? required = _spec.Requirements.Facets.OfType<EntityFacet>().FirstOrDefault();
                if (applicable == null || required == null)
                {
                    return;
                }

                IReadOnlyList<string>? candidates = applicable.Name switch
                {
                    SimpleValue simple => new[] { simple.Text },
                    RestrictionValue restriction when restriction.Patterns.Count == 0 && restriction.Enumerations.Count > 0
                        => restriction.Enumerations,
                    _ => null,
                };

                if (candidates == null)
                {
                    return;
                }

                if (!candidates.Any(name => Accepts(required.Name, name)))
                {
                    Error(DiagnosticCodes.ImpossibleEntityRequirement,
                        Tr.T("O requisito de entidade não aceita a entidade do applicability (") +
                        string.Join(", ", candidates) + Tr.T("): nenhum elemento pode passar."), required.Line);
                }
            }

            private static bool Accepts(IdsValue constraint, string value)
            {
                switch (constraint)
                {
                    case SimpleValue simple:
                        return simple.Text == value;
                    case RestrictionValue restriction:
                        if (restriction.Enumerations.Count > 0 && !restriction.Enumerations.Contains(value))
                        {
                            return false;
                        }

                        foreach (string pattern in restriction.Patterns)
                        {
                            if (XsdRegex.TryCreate(pattern, out Regex? regex, out _) && !regex!.IsMatch(value))
                            {
                                return false;
                            }
                        }

                        return true;
                    default:
                        return true;
                }
            }

            private void CheckRegexes(IdsValue? value)
            {
                if (value is RestrictionValue restriction)
                {
                    foreach (string pattern in restriction.Patterns)
                    {
                        if (!XsdRegex.TryCreate(pattern, out _, out string? error))
                        {
                            Error(DiagnosticCodes.InvalidRegex, Tr.T("Pattern inválido '") + pattern + "': " + error,
                                restriction.Line);
                        }
                    }
                }
            }

            private static string Hint(IfcValueKind kind)
            {
                switch (kind)
                {
                    case IfcValueKind.Boolean:
                        return Tr.T(" (use 'true' ou 'false', em minúsculas)");
                    case IfcValueKind.Integer:
                        return Tr.T(" (inteiro, sem casas decimais)");
                    case IfcValueKind.Real:
                        return Tr.T(" (número com ponto decimal, ex.: 42.3)");
                    default:
                        return "";
                }
            }

            private static string Describe(string? instructions) =>
                string.IsNullOrWhiteSpace(instructions) ? Tr.T(" (ausente)") : ": \"" + instructions + "\"";

            private static string DescribeUsage(SpecificationUsage usage)
            {
                switch (usage)
                {
                    case SpecificationUsage.Required:
                        return Tr.T("obrigatória");
                    case SpecificationUsage.Prohibited:
                        return Tr.T("proibida");
                    default:
                        return Tr.T("opcional");
                }
            }

            private void Error(string code, string message, int line) =>
                _out.Add(new IdsDiagnostic(DiagnosticSeverity.Error, code, message, _file, line, _spec.Name));

            private void Warning(string code, string message, int line) =>
                _out.Add(new IdsDiagnostic(DiagnosticSeverity.Warning, code, message, _file, line, _spec.Name));
        }
    }
}
