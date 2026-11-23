using System;
using System.Collections.Generic;
using BIMinFormation.Ids;

namespace BIMinFormation.Exporter
{
    /// <summary>Where the value a requirement asks for comes from in the IFC.</summary>
    public enum ValueOrigin
    {
        /// <summary>Entity and predefined type: written by the Classification (<c>IFC_EXPORT_ELEMENT_AS</c>).</summary>
        Entity,

        /// <summary>Standard Qto: the exporter calculates it (with "Export base quantities") or reads the Qto parameter.</summary>
        ExporterQuantity,

        /// <summary>Standard Pset: the exporter reads the <c>Pset.Property</c> parameter that Revit installs.</summary>
        StandardParameter,

        /// <summary>User set: plugin parameter and a line in the PropertySets file.</summary>
        CustomParameter,

        /// <summary>Attribute with an exporter override parameter (<c>IfcName</c>, <c>IfcObjectType</c>…).</summary>
        AttributeOverride,

        /// <summary>Classification through the <c>ClassificationCode</c> parameter.</summary>
        ClassificationCode,

        /// <summary>Comes from modeling (material, containment, aggregation) or can only be checked by hand.</summary>
        Modeling,

        /// <summary>This exporter version cannot write what the IDS asks for.</summary>
        NotExportable,
    }

    public sealed class OriginResult
    {
        public ValueOrigin Origin { get; }
        public string? Detail { get; }
        public StandardParameter? Parameter { get; }
        public ExporterDataType? DataType { get; }

        public OriginResult(ValueOrigin origin, string? detail = null, StandardParameter? parameter = null,
            ExporterDataType? dataType = null)
        {
            Origin = origin;
            Detail = detail;
            Parameter = parameter;
            DataType = dataType;
        }

        public override string ToString() => Origin + (Detail != null ? ": " + Detail : "");
    }

    /// <summary>
    /// Classifies each requirement by where its value comes from in the IFC, using the target schema catalog (standard
    /// Psets) and the exporter catalog (installed parameters, accepted types).
    /// </summary>
    public static class ValueOrigins
    {
        /// <summary>Attributes with an override parameter in the exporter.</summary>
        public static readonly IReadOnlyDictionary<string, string> AttributeOverrides =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = "IfcName",
                ["Description"] = "IfcDescription",
                ["ObjectType"] = "IfcObjectType",
                ["Tag"] = "IfcTag",
                ["LongName"] = "IfcLongName",
            };

        /// <summary>
        /// Results already computed per facet (the origin depends only on the facet and the catalogs): Fill In Parameters and
        /// the pre-validation ask about the same facet for every element.
        /// </summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Facet, Dictionary<(IfcSchemaCatalog, ExporterCatalog), OriginResult>> Known =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Facet, Dictionary<(IfcSchemaCatalog, ExporterCatalog), OriginResult>>();

        public static OriginResult Classify(Facet facet, IfcSchemaCatalog schema, ExporterCatalog exporter)
        {
            var known = Known.GetOrCreateValue(facet);
            lock (known)
            {
                if (known.TryGetValue((schema, exporter), out var cached)) return cached;
            }

            var result = Compute(facet, schema, exporter);
            lock (known)
            {
                known[(schema, exporter)] = result;
            }

            return result;
        }

        private static OriginResult Compute(Facet facet, IfcSchemaCatalog schema, ExporterCatalog exporter)
        {
            switch (facet)
            {
                case EntityFacet _:
                    return new OriginResult(ValueOrigin.Entity, Tr.T("Classificação (IFC_EXPORT_ELEMENT_AS e tipo predefinido)"));
                case PropertyFacet property:
                    return Property(property, schema, exporter);
                case AttributeFacet attribute:
                    return Attribute(attribute);
                case ClassificationFacet _:
                    return new OriginResult(ValueOrigin.ClassificationCode, Tr.T("parâmetro ClassificationCode ([Sistema]Código:Descrição)"));
                case MaterialFacet _:
                    return new OriginResult(ValueOrigin.Modeling, Tr.T("material do elemento no Revit"));
                case PartOfFacet _:
                    return new OriginResult(ValueOrigin.Modeling, Tr.T("relação do modelo (nível, montagem, grupo, hospedeiro)"));
                default:
                    return new OriginResult(ValueOrigin.Modeling);
            }
        }

        private static OriginResult Attribute(AttributeFacet facet)
        {
            if (!(facet.Name is SimpleValue name) || name.Text.Length == 0)
            {
                return new OriginResult(ValueOrigin.Modeling, Tr.T("atributo sem nome definido: conferir pelo texto de instructions"));
            }

            if (AttributeOverrides.TryGetValue(name.Text, out string? parameter))
            {
                return new OriginResult(ValueOrigin.AttributeOverride, Tr.T("parâmetro ") + parameter + Tr.T(" (ou ") + parameter + "[Type])");
            }

            if (name.Text == "PredefinedType")
            {
                return new OriginResult(ValueOrigin.Entity, Tr.T("Classificação (tipo predefinido)"));
            }

            return new OriginResult(ValueOrigin.Modeling, Tr.T("atributo ") + name.Text + Tr.T(" escrito pelo exportador"));
        }

        private static OriginResult Property(PropertyFacet facet, IfcSchemaCatalog schema, ExporterCatalog exporter)
        {
            if (!(facet.PropertySet is SimpleValue pset) || !(facet.BaseName is SimpleValue property))
            {
                return new OriginResult(ValueOrigin.Modeling, Tr.T("nome de conjunto ou propriedade por restrição: conferir à mão"));
            }

            if (schema.Version == IfcSchemaVersion.Ifc2X3 && pset.Text == "BaseQuantities")
            {
                // in IFC2X3 the base quantities go in an IfcElementQuantity called "BaseQuantities"
                return new OriginResult(ValueOrigin.ExporterQuantity, Tr.T("calculada com 'Exportar quantidades base' (BaseQuantities do IFC2X3)"));
            }

            bool isPset = pset.Text.StartsWith("Pset_", StringComparison.Ordinal);
            bool isQto = pset.Text.StartsWith("Qto_", StringComparison.Ordinal);
            var template = schema.PropertySet(pset.Text);

            // a Pset_ the exporter does not generate as the IDS asks (a Pset it does not know, a missing property, a type
            // different from the standard, a non-standard name) goes in a user set with the same name: checked in Revit 2023,
            // 2025, 2026 and 2027 that the name stays as it is (without the "e" prefix) and that a set with the same name as the
            // exporter's comes out separately and is read together by IfcOpenShell/IfcTester.
            string sameName = Tr.T("sai num conjunto do usuário com esse nome");
            if (isPset || isQto)
            {
                if (template == null)
                {
                    return Custom(facet, exporter, "'" + pset.Text + Tr.T("' não é um ") + (isPset ? "Pset" : "Qto") + Tr.T(" padrão de ")
                                                   + schema.Name + "; " + sameName);
                }

                var propertyTemplate = template.Property(property.Text);
                if (propertyTemplate == null)
                {
                    return isPset
                        ? Custom(facet, exporter, "'" + property.Text + Tr.T("' não faz parte de ") + pset.Text + Tr.T(" em ") + schema.Name
                                                  + "; " + sameName)
                        : new OriginResult(ValueOrigin.NotExportable,
                            "'" + property.Text + Tr.T("' não faz parte de ") + pset.Text + Tr.T(" em ") + schema.Name
                            + Tr.T(": o exportador só calcula as quantidades do conjunto padrão"));
                }

                if (facet.DataType != null && propertyTemplate.DataType != null
                    && !string.Equals(facet.DataType, propertyTemplate.DataType, StringComparison.OrdinalIgnoreCase))
                {
                    return isPset
                        ? Custom(facet, exporter, pset.Text + "." + property.Text + Tr.T(" é ") + propertyTemplate.DataType
                                                  + Tr.T(" no padrão, mas o IDS pede ") + facet.DataType + "; " + sameName)
                        : new OriginResult(ValueOrigin.NotExportable,
                            pset.Text + "." + property.Text + Tr.T(" sai como ") + propertyTemplate.DataType
                            + Tr.T(" (padrão), mas o IDS pede ") + facet.DataType);
                }

                var parameter = exporter.FindStandardParameter(pset.Text, property.Text, ParameterScope.Instance)
                                ?? exporter.FindStandardParameter(pset.Text, property.Text, ParameterScope.Type);
                if (isQto)
                {
                    return new OriginResult(ValueOrigin.ExporterQuantity,
                        Tr.T("calculada com 'Exportar quantidades base'")
                        + (parameter != null ? Tr.T(", ou pelo parâmetro ") + parameter.ParameterName : ""), parameter);
                }

                // the installed parameters mirror the exporter Pset definitions: without a parameter (or without the type in the
                // PropertyType enum), the version does not write the property
                bool exporterType = propertyTemplate.DataType == null
                                    || DataTypeMap.Resolve(propertyTemplate.DataType, exporter).PropertyType != null;
                if (parameter == null || !exporterType)
                {
                    return Custom(facet, exporter, Tr.T("o exportador do Revit ") + exporter.Year + Tr.T(" não gera ") + pset.Text + "."
                                                   + property.Text + "; " + sameName, propertyTemplate.DataType);
                }

                return new OriginResult(ValueOrigin.StandardParameter, Tr.T("parâmetro ") + parameter.ParameterName, parameter);
            }

            return Custom(facet, exporter, null);
        }

        /// <summary>User set property; without a dataType in the IDS, the standard Pset's type or text applies.</summary>
        private static OriginResult Custom(PropertyFacet facet, ExporterCatalog exporter, string? note, string? standardType = null)
        {
            if (facet.DataType == null && standardType != null)
            {
                var standard = DataTypeMap.Resolve(standardType, exporter);
                if (standard.IsSupported)
                {
                    return new OriginResult(ValueOrigin.CustomParameter, Join(note, standard.Note), dataType: standard);
                }
            }

            if (facet.DataType == null)
            {
                return new OriginResult(ValueOrigin.CustomParameter,
                    Join(note, Tr.T("sem dataType no IDS: sai como texto (Label)")),
                    dataType: DataTypeMap.Resolve("IFCLABEL", exporter));
            }

            var dataType = DataTypeMap.Resolve(facet.DataType, exporter);
            return dataType.IsSupported
                ? new OriginResult(ValueOrigin.CustomParameter, Join(note, dataType.Note), dataType: dataType)
                : new OriginResult(ValueOrigin.NotExportable, Join(note, dataType.Note), dataType: dataType);
        }

        private static string? Join(string? a, string? b) =>
            a == null ? b : b == null ? a : a + "; " + b;
    }
}
