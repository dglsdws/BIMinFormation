using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BIMinFormation.Core.Classification;
using BIMinFormation.Exporter;
using BIMinFormation.Ids;

namespace BIMinFormation.Core.Prevalidation
{
    /// <summary>Name and category of the element in Revit, for the reports.</summary>
    public sealed class ElementInfo
    {
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";
    }

    /// <summary>
    /// Pre-validation outputs: pending items in CSV, BCF 2.1 topics and the report for the client with progress and the
    /// software limits.
    /// </summary>
    public static class PrevalidationReports
    {
        private static string GlobalId(Rules.ElementState element) => element.GlobalId ?? "";

        /// <summary>Requirements that need action: they fail, or stay to check without being an exporter quantity.</summary>
        public static IEnumerable<RequirementCheck> Pending(ElementCheck element) =>
            element.Requirements.Where(r => r.Status == CheckStatus.Fail || (r.Status == CheckStatus.Unknown && !r.ByExporter));

        // ---------------------------------------------------------------- CSV

        /// <summary>One line per element and pending requirement (";" separator, for Excel in Portuguese).</summary>
        public static string PendingCsv(PrevalidationResult result, Func<string, ElementInfo> info)
        {
            var csv = new StringBuilder();
            Line(csv, Tr.T("Ficha"), Tr.T("Elemento"), Tr.T("Categoria"), Tr.T("ID Revit"), Tr.T("GlobalId"), Tr.T("Situação do elemento"), Tr.T("Exigência"), Tr.T("Situação"), Tr.T("Motivo"));
            foreach (var orphan in result.Orphans)
            {
                var element = info(orphan.Element.Id);
                Line(csv, Tr.T("(identidade sem ficha)"), element.Name, element.Category, orphan.Element.Id, GlobalId(orphan.Element), Tr.T("falha"),
                    Tr.T("identidade"), Tr.T("falha"), orphan.OrphanValue + Tr.T(" não é o nome de nenhuma ficha"));
            }

            foreach (var specification in result.Specifications)
            {
                foreach (var element in specification.Elements)
                {
                    var about = info(element.Element.Id);
                    string status = element.OkInModel ? Tr.T("ok no modelo") : Text(element.Status);
                    foreach (var requirement in Pending(element))
                    {
                        Line(csv, specification.Specification.Name, about.Name, about.Category, element.Element.Id, GlobalId(element.Element),
                            status, requirement.Label, Text(requirement.Status), requirement.Reason ?? "");
                    }
                }
            }

            return csv.ToString();
        }

        /// <summary>
        /// Project specification table for the BEP: every specification in the folder, first the ones in use (with elements in
        /// the model), with file, system, elements and status, then the ones without elements. List only: the plugin does not
        /// generate .ids files.
        /// </summary>
        public static string SpecificationsCsv(PrevalidationResult result, IdsLibrary library, string? idsFolder)
        {
            var files = new Dictionary<Specification, string>();
            foreach (var document in library.Documents)
            {
                string path = document.FilePath ?? "";
                if (idsFolder != null && path.StartsWith(idsFolder, StringComparison.OrdinalIgnoreCase))
                {
                    path = path.Substring(idsFolder.Length).TrimStart('\\', '/');
                }

                foreach (var specification in document.Specifications) files[specification] = path;
            }

            var csv = new StringBuilder();
            Line(csv, Tr.T("Ficha"), Tr.T("Em uso"), Tr.T("Arquivo .ids"), Tr.T("Sistema"), Tr.T("Elementos"), Tr.T("Ok"), Tr.T("Falham"), Tr.T("A conferir"), Tr.T("Uso no .ids"));
            foreach (var check in result.Specifications.OrderBy(s => s.Elements.Count == 0)
                         .ThenBy(s => s.Specification.Name, PortugueseComparer.Instance))
            {
                var specification = check.Specification;
                bool used = check.Elements.Count > 0;
                Line(csv, specification.Name, used ? Tr.T("sim") : Tr.T("não"), files.TryGetValue(specification, out var file) ? file : "",
                    System(specification.Name) ?? "", check.Elements.Count.ToString(CultureInfo.InvariantCulture),
                    used ? check.Ok.ToString(CultureInfo.InvariantCulture) : "", used ? check.Failed.ToString(CultureInfo.InvariantCulture) : "",
                    used ? check.ToCheck.ToString(CultureInfo.InvariantCulture) : "",
                    specification.Usage == SpecificationUsage.Required ? Tr.T("obrigatória") : specification.Usage == SpecificationUsage.Prohibited ? Tr.T("proibida") : Tr.T("opcional"));
            }

            return csv.ToString();
        }

        private static void Line(StringBuilder csv, params string[] fields)
        {
            csv.Append(string.Join(";", fields.Select(f => f.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0
                ? "\"" + f.Replace("\"", "\"\"") + "\""
                : f))).Append("\r\n");
        }

        private static string Text(CheckStatus status) => status == CheckStatus.Pass ? Tr.T("passa") : status == CheckStatus.Fail ? Tr.T("falha") : Tr.T("a conferir");

        // ---------------------------------------------------------------- BCF

        /// <summary>
        /// BCF 2.1: one topic per failing element in each specification (and per identity without a specification), with the
        /// element selected and colored red by GlobalId. Stable topic GUIDs (same specification and element, same topic).
        /// </summary>
        public static int WriteBcf(Stream stream, PrevalidationResult result, Func<string, ElementInfo> info, string project, DateTime now)
        {
            var topics = new List<(Guid Id, string Title, string Description, string GlobalId)>();
            foreach (var orphan in result.Orphans)
            {
                var element = info(orphan.Element.Id);
                topics.Add((Stable("orphan", GlobalId(orphan.Element)), Tr.T("Identidade sem ficha: ") + element.Name,
                    orphan.OrphanValue + Tr.T(" não é o nome de nenhuma ficha do .ids."), GlobalId(orphan.Element)));
            }

            foreach (var specification in result.Specifications)
            {
                foreach (var element in specification.Elements.Where(e => e.Status == CheckStatus.Fail))
                {
                    var about = info(element.Element.Id);
                    var lines = element.Requirements.Where(r => r.Status == CheckStatus.Fail).Select(r => "• " + r).ToList();
                    // project, building and site have no element GlobalId: the stable topic id comes from the Revit id
                    topics.Add((Stable(specification.Specification.Name, element.Element.GlobalId ?? element.Element.Id),
                        specification.Specification.Name + ": " + about.Name,
                        Tr.T("Falha na pré-validação (") + about.Category + Tr.T(", ID Revit ") + element.Element.Id + "):\n" + string.Join("\n", lines),
                        GlobalId(element.Element)));
                }
            }

            string date = now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            Write(zip, "bcf.version", new XElement("Version", new XAttribute("VersionId", "2.1"), new XElement("DetailedVersion", "2.1")));
            Write(zip, "project.bcfp", new XElement("ProjectExtension",
                new XElement("Project", new XAttribute("ProjectId", Stable("project", project).ToString()), new XElement("Name", project)),
                new XElement("ExtensionSchema")));
            foreach (var topic in topics)
            {
                var viewpoint = Stable("viewpoint", topic.Id.ToString());
                Write(zip, topic.Id + "/markup.bcf", new XElement("Markup",
                    new XElement("Topic", new XAttribute("Guid", topic.Id.ToString()), new XAttribute("TopicType", "Error"),
                        new XAttribute("TopicStatus", "Open"),
                        new XElement("Title", topic.Title),
                        new XElement("Labels", "BIMinFormation"),
                        new XElement("CreationDate", date),
                        new XElement("CreationAuthor", "BIMinFormation"),
                        new XElement("Description", topic.Description)),
                    new XElement("Viewpoints", new XAttribute("Guid", viewpoint.ToString()), new XElement("Viewpoint", "viewpoint.bcfv"))));
                var component = new XElement("Component", new XAttribute("IfcGuid", topic.GlobalId));
                Write(zip, topic.Id + "/viewpoint.bcfv", new XElement("VisualizationInfo", new XAttribute("Guid", viewpoint.ToString()),
                    topic.GlobalId.Length == 0
                        ? new XElement("Components", new XElement("Visibility", new XAttribute("DefaultVisibility", "true")))
                        : new XElement("Components",
                            new XElement("Selection", component),
                            new XElement("Visibility", new XAttribute("DefaultVisibility", "true")),
                            new XElement("Coloring", new XElement("Color", new XAttribute("Color", "FFD63031"), new XElement(component))))));
            }

            return topics.Count;
        }

        private static void Write(ZipArchive zip, string name, XElement root)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            new XDocument(new XDeclaration("1.0", "UTF-8", null), root).Save(writer);
        }

        /// <summary>Stable GUID (SHA-1 of the text, version 5).</summary>
        private static Guid Stable(string kind, string key)
        {
            using var sha = SHA1.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes("BIMinFormation.BCF/" + kind + "/" + key));
            var bytes = new byte[16];
            Array.Copy(hash, bytes, 16);
            bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50);
            bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
            return new Guid(bytes);
        }

        // ---------------------------------------------------------------- report

        /// <summary>
        /// Report for the client (HTML): progress per specification and per system, what the software cannot deliver as the
        /// .ids asks, and the defects found in the .ids files.
        /// </summary>
        public static string ContractorHtml(PrevalidationResult result, IdsLibrary library, IfcSchemaVersion schema, ExporterCatalog exporter,
            string project, DateTime now)
        {
            var html = new StringBuilder();
            string E(string text) => WebUtility.HtmlEncode(text);
            var used = result.Specifications.Where(s => s.Elements.Count > 0).ToList();
            int total = used.Sum(s => s.Elements.Count);
            int ok = used.Sum(s => s.Ok);
            string Percent(int part, int all) => all == 0 ? "—" : (100.0 * part / all).ToString("0", CultureInfo.InvariantCulture) + "%";

            html.Append(Tr.T("<!doctype html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\"><title>"))
                .Append(E(Tr.T("BIMinFormation — ") + project)).Append("</title><style>")
                .Append(("body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#222}h1{font-size:20px}h2{font-size:16px;margin-top:28px}"))
                .Append(("table{border-collapse:collapse;margin-top:8px}th,td{border:1px solid #ccc;padding:4px 8px;font-size:13px;text-align:left}"))
                .Append("th{background:#f2f2f2}.n{text-align:right}.f{color:#c0392b}.c{color:#b9770e}.o{color:#1e8449}.m{color:#777;font-size:12px}")
                .Append("</style></head><body>");
            html.Append("<h1>").Append(E(project)).Append(Tr.T("</h1><p class=\"m\">Pré-validação do BIMinFormation em "))
                .Append(now.ToString(Tr.T("dd/MM/yyyy HH:mm"), CultureInfo.InvariantCulture)).Append(Tr.T(" · esquema ")).Append(E(schema.ToString()))
                .Append(Tr.T(" · exportador do Revit ")).Append(exporter.Year).Append("</p>");

            html.Append(Tr.T("<h2>Resumo</h2><p>Fichas ativas: ")).Append(result.Specifications.Count).Append(Tr.T(" · com elementos no modelo: "))
                .Append(used.Count).Append(Tr.T(" · verificações: ")).Append(total).Append(Tr.T(" · <span class=\"o\">ok ")).Append(ok).Append(" (")
                .Append(Percent(ok, total)).Append(Tr.T(")</span> · <span class=\"f\">falham ")).Append(used.Sum(s => s.Failed))
                .Append(Tr.T("</span> · <span class=\"c\">a conferir ")).Append(used.Sum(s => s.ToCheck)).Append("</span>")
                .Append(result.Orphans.Count > 0 ? Tr.T(" · <span class=\"f\">identidade sem ficha ") + result.Orphans.Count + "</span>" : "")
                .Append(Tr.T("</p><p class=\"m\">Ok: o modelo atende (as quantidades, quando pedidas, são calculadas pelo exportador). "))
                .Append(Tr.T("A conferir: só o IFC confirma (material, classificação, Pset padrão vazio que o exportador pode calcular).</p>"));

            var systems = used.GroupBy(s => System(s.Specification.Name)).Where(g => g.Key != null).ToList();
            if (systems.Count > 1)
            {
                html.Append(Tr.T("<h2>Progresso por sistema</h2><table><tr><th>Sistema</th><th>Fichas</th><th>Verificações</th><th>Ok</th><th>Falham</th><th>A conferir</th><th>% ok</th></tr>"));
                foreach (var system in systems.OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    int all = system.Sum(s => s.Elements.Count), good = system.Sum(s => s.Ok);
                    html.Append("<tr><td>").Append(E(system.Key!)).Append(Tr.T("</td><td class=\"n\">")).Append(system.Count())
                        .Append(Tr.T("</td><td class=\"n\">")).Append(all).Append(Tr.T("</td><td class=\"n o\">")).Append(good)
                        .Append(Tr.T("</td><td class=\"n f\">")).Append(system.Sum(s => s.Failed)).Append(Tr.T("</td><td class=\"n c\">"))
                        .Append(system.Sum(s => s.ToCheck)).Append(Tr.T("</td><td class=\"n\">")).Append(Percent(good, all)).Append("</td></tr>");
                }

                html.Append("</table>");
            }

            html.Append(Tr.T("<h2>Progresso por ficha</h2>"));
            if (used.Count == 0)
            {
                html.Append(Tr.T("<p>Nenhum elemento com ficha no modelo.</p>"));
            }
            else
            {
                html.Append(Tr.T("<table><tr><th>Ficha</th><th>Elementos</th><th>Ok</th><th>Falham</th><th>A conferir</th><th>% ok</th><th>Pendências mais comuns</th></tr>"));
                foreach (var check in used.OrderBy(s => s.Specification.Name, PortugueseComparer.Instance))
                {
                    var common = check.Elements.SelectMany(Pending).GroupBy(r => r.Label).OrderByDescending(g => g.Count()).Take(3)
                        .Select(g => g.Key + " (" + g.Count() + ")");
                    html.Append("<tr><td>").Append(E(check.Specification.Name)).Append(Tr.T("</td><td class=\"n\">")).Append(check.Elements.Count)
                        .Append(Tr.T("</td><td class=\"n o\">")).Append(check.Ok).Append(Tr.T("</td><td class=\"n f\">")).Append(check.Failed)
                        .Append(Tr.T("</td><td class=\"n c\">")).Append(check.ToCheck).Append(Tr.T("</td><td class=\"n\">"))
                        .Append(Percent(check.Ok, check.Elements.Count)).Append("</td><td>").Append(E(string.Join(", ", common))).Append("</td></tr>");
                }

                html.Append("</table>");
            }

            // what the software cannot deliver as the .ids asks
            var catalog = IfcSchemaCatalog.For(schema);
            var limits = new List<(string Spec, string What, string Why)>();
            foreach (var specification in library.Specifications)
            {
                var action = ClassificationAction.For(specification, schema);
                foreach (string problem in action.Problems) limits.Add((specification.Name, Tr.T("classificação"), problem));
                if (action.ObjectType != null)
                {
                    limits.Add((specification.Name, Tr.T("tipo predefinido"),
                        action.ObjectType + Tr.T(" não existe em ") + schema + Tr.T(": sai USERDEFINED com ObjectType = ") + action.ObjectType));
                }

                foreach (var facet in specification.Requirements.Facets)
                {
                    var origin = ValueOrigins.Classify(facet, catalog, exporter);
                    if (origin.Origin == ValueOrigin.NotExportable) limits.Add((specification.Name, Prevalidator.Label(facet), origin.Detail ?? ""));
                }
            }

            html.Append(Tr.T("<h2>O que o software não entrega como o .ids pede</h2>"));
            if (limits.Count == 0)
            {
                html.Append(Tr.T("<p>Nenhum limite encontrado para as fichas ativas.</p>"));
            }
            else
            {
                html.Append(Tr.T("<p class=\"m\">Exigências que o exportador do Revit ")).Append(exporter.Year)
                    .Append(Tr.T(" não escreve como pedido, ou que dependem de um ajuste do Protocolo. Independem do modelo.</p>"))
                    .Append(Tr.T("<table><tr><th>Ficha</th><th>Exigência</th><th>Motivo</th></tr>"));
                foreach (var limit in limits.OrderBy(l => l.Spec, PortugueseComparer.Instance))
                {
                    html.Append("<tr><td>").Append(E(limit.Spec)).Append("</td><td>").Append(E(limit.What)).Append("</td><td>")
                        .Append(E(limit.Why)).Append("</td></tr>");
                }

                html.Append("</table>");
            }

            var defects = library.Diagnostics.ToList();
            html.Append(Tr.T("<h2>Defeitos encontrados nos .ids</h2>"));
            if (defects.Count == 0)
            {
                html.Append("<p>" + Tr.T("Nenhum.") + "</p>");
            }
            else
            {
                html.Append(Tr.T("<p class=\"m\">Problemas de redação nos arquivos recebidos; corrigir é com o autor do .ids.</p>"))
                    .Append(Tr.T("<table><tr><th>Arquivo</th><th>Ficha</th><th>Código</th><th>Problema</th></tr>"));
                foreach (var defect in defects.Take(300))
                {
                    html.Append("<tr><td>").Append(E(defect.FilePath == null ? "" : Path.GetFileName(defect.FilePath) + (defect.Line > 0 ? ":" + defect.Line : "")))
                        .Append("</td><td>").Append(E(defect.SpecificationName ?? "")).Append("</td><td>").Append(E(defect.Code))
                        .Append("</td><td>").Append(E(defect.Message)).Append("</td></tr>");
                }

                html.Append("</table>");
                if (defects.Count > 300) html.Append(Tr.T("<p class=\"m\">… e mais ")).Append(defects.Count - 300).Append(".</p>");
            }

            return html.Append("</body></html>").ToString();
        }

        /// <summary>System from the specification name (<c>PR.08.45 PAREDE</c> → <c>PR.08</c>); <c>null</c> without that pattern.</summary>
        public static string? System(string specification)
        {
            var match = Regex.Match(specification, @"^([A-Z]{1,4}\.\d{1,3})\.\d");
            return match.Success ? match.Groups[1].Value : null;
        }
    }
}
