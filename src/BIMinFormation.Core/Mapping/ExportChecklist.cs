using System.Collections.Generic;
using System.Linq;
using BIMinFormation.Core.Profile;
using BIMinFormation.Exporter;
using BIMinFormation.Ids;

namespace BIMinFormation.Core.Mapping
{
    /// <summary>
    /// What to set in the Revit IFC export window so that the file meets the specifications: IFC version, base
    /// quantities, standard sets, PropertySets file, classification.
    /// </summary>
    public static class ExportChecklist
    {
        public static IReadOnlyList<string> Build(IEnumerable<Specification> specifications, IfcSchemaVersion schema,
            ExporterCatalog exporter, ProjectProfile profile, MappingFormat format = MappingFormat.PerEntity)
        {
            var catalog = IfcSchemaCatalog.For(schema);
            var origins = specifications
                .SelectMany(s => s.Requirements.Facets)
                .Select(f => ValueOrigins.Classify(f, catalog, exporter).Origin)
                .ToList();
            var items = new List<string>();

            switch (schema)
            {
                case IfcSchemaVersion.Ifc2X3:
                    items.Add(Tr.T("Versão IFC: IFC 2x3 Coordination View 2.0."));
                    break;
                case IfcSchemaVersion.Ifc4:
                    items.Add(Tr.T("Versão IFC: IFC4 Design Transfer View. A Reference View descarta conjuntos fora da lista ")
                              + Tr.T("certificada (ex.: Pset_DoorWindowGlazingType, Pset_CoveringFlooring)."));
                    break;
                default:
                    if (exporter.Ifc4x3WithQuantities)
                    {
                        items.Add(Tr.T("Versão IFC: IFC 4.3 Vista de Transferência de Projeto. O Revit ") + exporter.Year
                                  + Tr.T(" grava IFC4X3_ADD2 com quantidades; a Vista de Referência não grava quantidades."));
                    }
                    else if (exporter.Year >= 2025 && exporter.CanExport(schema))
                    {
                        items.Add(Tr.T("Versão IFC: o Revit ") + exporter.Year + Tr.T(" tem só a Vista de Referência IFC4.3, que grava IFC4X3_ADD2 ")
                                  + Tr.T("mas não grava quantidades (Qto_); para entregar, exporte do Revit 2026 ou mais novo."));
                    }
                    else
                    {
                        items.Add(exporter.CanExport(schema)
                            ? Tr.T("Versão IFC: IFC 4.3. Atenção: o exportador do Revit 2023 grava o cabeçalho IFC4X3 (não IFC4X3_ADD2) ")
                              + Tr.T("e não exporta quantidades no 4.3 experimental; para entregar, use o Revit 2026 ou mais novo.")
                            : Tr.T("Versão IFC: o Revit ") + exporter.Year + Tr.T(" não exporta IFC 4.3 (IFC4X3_ADD2). Exporte de uma versão ")
                              + Tr.T("do Revit que tenha essa opção; o arquivo de PropertySets gerado continua válido."));
                    }

                    break;
            }

            if (format == MappingFormat.PerPropertySet)
            {
                items.Add(Tr.T("Revit 2027: os conjuntos do usuário ficam gravados no projeto. O botão Exportar do plugin grava os ")
                          + Tr.T("conjuntos e o modelo de parâmetros da configuração; com o arquivo avulso das Configurações, importe-o na aba ")
                          + Tr.T("de conjuntos de propriedades da exportação e reimporte sempre que gerar o arquivo de novo."));
                items.Add(Tr.T("Revit 2027: deixe ligados os conjuntos de propriedades comuns do IFC e desligados os conjuntos do ")
                          + Tr.T("Revit (grupos como \"Cotas\" e \"Fases\" não são pedidos pelas fichas)."));
            }

            if (origins.Contains(ValueOrigin.ExporterQuantity))
            {
                items.Add(Tr.T("Marque \"Exportar quantidades base\" (as fichas pedem conjuntos Qto_)."));
            }

            if (origins.Contains(ValueOrigin.StandardParameter))
            {
                items.Add(Tr.T("Marque \"Exportar conjuntos de propriedades comuns do IFC\" (as fichas pedem Psets padrão)."));
            }

            if (origins.Contains(ValueOrigin.CustomParameter) || specifications.Any(s => s.Applicability.Facets.OfType<PropertyFacet>().Any()))
            {
                items.Add(Tr.T("Marque \"Exportar conjuntos de propriedades definidos pelo usuário\" e escolha o arquivo ")
                          + (string.IsNullOrEmpty(profile.PropertySetsFile) ? Tr.T("gerado pelo plugin") : profile.PropertySetsFile) + ".");
            }

            if (origins.Contains(ValueOrigin.ClassificationCode))
            {
                items.Add(Tr.T("Confira em \"Configurações de classificação\" o sistema usado no parâmetro ClassificationCode."));
            }

            if (origins.Contains(ValueOrigin.AttributeOverride))
            {
                items.Add(Tr.T("Os atributos Name, Description, ObjectType, Tag e LongName saem dos parâmetros IfcName, ")
                          + Tr.T("IfcDescription, IfcObjectType, IfcTag e IfcLongName quando preenchidos."));
            }

            items.Add(Tr.T("Exporte os elementos visíveis na vista ou o modelo inteiro conforme o BEP, e valide o IFC com os ")
                      + Tr.T(".ids recebidos antes da entrega."));
            return items;
        }
    }
}
