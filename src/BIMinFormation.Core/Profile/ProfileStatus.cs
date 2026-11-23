using System.Collections.Generic;
using System.IO;
using System.Linq;
using BIMinFormation.Exporter;
using BIMinFormation.Ids;

namespace BIMinFormation.Core.Profile
{
    public enum StatusLevel
    {
        Ok,
        Warning,
        Error,
    }

    /// <summary>Status line of the Settings window and the result of reading the <c>.ids</c> files.</summary>
    public sealed class ProfileStatus
    {
        public StatusLevel Level { get; }

        /// <summary>One-line summary.</summary>
        public string Summary { get; }

        /// <summary>Warnings and errors, one per line.</summary>
        public IReadOnlyList<string> Messages { get; }

        public IdsLibrary? Library { get; }

        /// <summary>Effective schema: the profile's, or the only one used by the files.</summary>
        public IfcSchemaVersion? Schema { get; }

        private ProfileStatus(StatusLevel level, string summary, IReadOnlyList<string> messages, IdsLibrary? library,
            IfcSchemaVersion? schema)
        {
            Level = level;
            Summary = summary;
            Messages = messages;
            Library = library;
            Schema = schema;
        }

        public override string ToString() => Level + ": " + Summary;

        /// <summary>Reads the profile <c>.ids</c> files and describes the situation for the user.</summary>
        public static ProfileStatus Evaluate(ProjectProfile profile, ExporterCatalog exporter)
        {
            if (string.IsNullOrWhiteSpace(profile.IdsFolder))
            {
                return Fail(Tr.T("Escolha a pasta com os arquivos .ids recebidos do contratante."));
            }

            if (!Directory.Exists(profile.IdsFolder))
            {
                return Fail(Tr.T("Pasta não encontrada: ") + profile.IdsFolder);
            }

            var files = profile.ResolveFiles();
            var missing = files.Where(f => !File.Exists(f)).ToList();
            var present = files.Except(missing).ToList();
            var messages = missing.Select(f => Tr.T("Arquivo do perfil não encontrado: ") + Path.GetFileName(f)).ToList();
            if (present.Count == 0)
            {
                return new ProfileStatus(missing.Count > 0 ? StatusLevel.Error : StatusLevel.Warning,
                    Tr.T("Nenhum arquivo .ids na pasta."), messages, null, null);
            }

            var library = IdsLibrary.LoadFiles(present);
            int errors = library.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
            int warnings = library.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
            if (errors > 0)
            {
                messages.Add(errors + Tr.T(" erro(s) nos .ids: as fichas afetadas podem não ser verificáveis."));
            }

            if (library.Diagnostics.Any(d => d.Code == DiagnosticCodes.DuplicateSpecification))
            {
                messages.Add(Tr.T("A mesma ficha aparece em mais de um arquivo (ex.: fases diferentes): escolha só os arquivos da fase."));
            }

            var versions = library.Specifications.SelectMany(s => s.IfcVersions).Distinct().OrderBy(v => v).ToList();
            if (versions.Count > 1)
            {
                messages.Add(Tr.T("Versões IFC misturadas nos arquivos: ") + string.Join(", ", versions.Select(Name)) + ".");
            }

            IfcSchemaVersion? schema = profile.Schema ?? (versions.Count == 1 ? versions[0] : (IfcSchemaVersion?)null);
            if (profile.TargetSchema != null && profile.Schema == null)
            {
                messages.Add(Tr.T("Esquema alvo do perfil inválido: ") + profile.TargetSchema + ".");
            }
            else if (schema == null)
            {
                messages.Add(Tr.T("Escolha o esquema IFC alvo no perfil."));
            }
            else
            {
                if (profile.Schema != null && versions.Count > 0 && !versions.Contains(schema.Value))
                {
                    messages.Add(Tr.T("O esquema alvo ") + Name(schema.Value) + Tr.T(" não está entre os declarados nos .ids."));
                }

                if (schema.Value == IfcSchemaVersion.Ifc4X3Add2 && exporter.Year < 2025 && exporter.CanExport(schema.Value))
                {
                    // 2023.1/2024: only the experimental 4.3 (IFC4X3 header, no quantities; checked in 2023.1)
                    messages.Add(Tr.T("O exportador do Revit ") + exporter.Year + " (" + exporter.ExporterVersion + Tr.T(") só tem o IFC 4.3 ")
                                 + Tr.T("experimental: grava o cabeçalho IFC4X3 (não IFC4X3_ADD2) e não exporta quantidades. O plugin ajuda ")
                                 + Tr.T("a preencher e a pré-validar, mas o IFC entregável precisa do Revit 2026 ou mais novo."));
                }
                else if (schema.Value == IfcSchemaVersion.Ifc4X3Add2 && exporter.CanExport(schema.Value) && !exporter.Ifc4x3WithQuantities)
                {
                    // 2025: IFC 4.3 only has the Reference View, which writes no quantities (checked in 25.5.10)
                    messages.Add(Tr.T("O Revit ") + exporter.Year + Tr.T(" exporta IFC 4.3 só na Vista de Referência, que não grava quantidades ")
                                 + Tr.T("(Qto_). O plugin ajuda a preencher e a pré-validar, mas o IFC entregável precisa do Revit 2026 ou ")
                                 + Tr.T("mais novo (Vista de Transferência de Projeto IFC4x3)."));
                }
                else if (!exporter.CanExport(schema.Value))
                {
                    messages.Add(Tr.T("O exportador do Revit ") + exporter.Year + " (" + exporter.ExporterVersion + Tr.T(") não exporta ")
                                 + Name(schema.Value) + Tr.T(". O plugin ajuda a preencher e a pré-validar, mas o IFC ")
                                 + Tr.T("entregável precisa de uma versão do Revit que exporte esse esquema."));
                }
            }

            string summary = library.Documents.Count + Tr.T(" arquivo(s), ") + library.Specifications.Count + Tr.T(" ficha(s)")
                             + (schema != null ? ", " + Name(schema.Value) : "")
                             + (warnings > 0 ? ", " + warnings + Tr.T(" aviso(s)") : "") + ".";
            var level = missing.Count > 0 || errors > 0 || messages.Count > 0 ? StatusLevel.Warning : StatusLevel.Ok;
            return new ProfileStatus(level, summary, messages, library, schema);
        }

        public static string Name(IfcSchemaVersion version) =>
            version switch
            {
                IfcSchemaVersion.Ifc2X3 => "IFC2X3",
                IfcSchemaVersion.Ifc4 => "IFC4",
                _ => "IFC4X3_ADD2",
            };

        private static ProfileStatus Fail(string message) =>
            new ProfileStatus(StatusLevel.Error, message, new[] { message }, null, null);
    }
}
