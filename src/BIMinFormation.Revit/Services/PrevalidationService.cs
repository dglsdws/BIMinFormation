using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using BIMinFormation.Core.Prevalidation;
using BIMinFormation.Ids;

namespace BIMinFormation.Revit.Services
{
    /// <summary>Pre-validation of the document with the profile specifications.</summary>
    public static class PrevalidationService
    {
        /// <summary>Name and category of each model element (for the reports); read in the API context.</summary>
        public static Dictionary<string, ElementInfo> Infos(Document document)
        {
            var model = Adapter.ModelReader.ModelElements(document);
            var infos = model.Concat(Adapter.ModelReader.Storeys(document, model)).ToDictionary(e => e.Id.IdValue().ToString(),
                e => new ElementInfo { Name = e.Name, Category = e.Category?.Name ?? "" });
            var project = document.ProjectInformation;
            if (project != null)
            {
                string id = project.Id.IdValue().ToString(), category = project.Category?.Name ?? "";
                infos[id + Adapter.ModelReader.ProjectSuffix] = new ElementInfo { Name = Tr.T("Projeto"), Category = category };
                infos[id] = new ElementInfo { Name = Tr.T("Edifício"), Category = category };
                infos[id + Adapter.ModelReader.SiteSuffix] = new ElementInfo { Name = Tr.T("Terreno"), Category = category };
            }

            return infos;
        }

        /// <summary>Project specification table for the BEP (CSV with BOM, for Excel).</summary>
        public static void WriteSpecificationTable(PrevalidationResult result, PluginContext context, string path) =>
            System.IO.File.WriteAllText(path, PrevalidationReports.SpecificationsCsv(result, context.Library, context.Profile.IdsFolder),
                new UTF8Encoding(true));

        /// <summary>Writes pending items (CSV with BOM, for Excel), BCF and report; returns how many BCF topics.</summary>
        public static int WriteReports(PrevalidationResult result, PluginContext context, IReadOnlyDictionary<string, ElementInfo> infos,
            string project, string? csv, string? bcf, string? html)
        {
            ElementInfo Info(string id) => infos.TryGetValue(id, out var info) ? info : new ElementInfo { Name = "ID " + id };
            if (csv != null) System.IO.File.WriteAllText(csv, PrevalidationReports.PendingCsv(result, Info), new UTF8Encoding(true));
            int topics = 0;
            if (bcf != null)
            {
                using var stream = System.IO.File.Create(bcf);
                topics = PrevalidationReports.WriteBcf(stream, result, Info, project, System.DateTime.Now);
            }

            if (html != null)
            {
                System.IO.File.WriteAllText(html, PrevalidationReports.ContractorHtml(result, context.Library, context.Schema, context.Exporter,
                    project, System.DateTime.Now), new UTF8Encoding(false));
            }

            return topics;
        }

        public static PrevalidationResult Run(Document document, PluginContext context)
        {
            var model = Adapter.ModelCache.Read(document, context);
            return Prevalidator.Run(context.Library.Specifications, model, context.SchemaCatalog, context.Exporter);
        }

        /// <summary>
        /// Color of each element with a specification (the worst among its specifications): fail, to check, ok (pass or ok in
        /// the model). An identity without a specification counts as a failure.
        /// </summary>
        public static Dictionary<ElementId, Adapter.ViewColor> Colors(PrevalidationResult result)
        {
            var colors = new Dictionary<ElementId, Adapter.ViewColor>();
            void Mark(string id, Adapter.ViewColor color)
            {
                var key = Adapter.ModelReader.ElementIdOf(id);
                if (!colors.TryGetValue(key, out var current) || color > current) colors[key] = color;
            }

            // storeys, project and building (and a site from Project Information) are not drawn in the view
            foreach (var element in result.Specifications.SelectMany(s => s.Elements)
                         .Where(e => e.Element.Entity is not ("IFCBUILDINGSTOREY" or "IFCPROJECT" or "IFCBUILDING") && !e.Element.Id.Contains("#")))
            {
                Mark(element.Element.Id, element.Status == CheckStatus.Fail ? Adapter.ViewColor.Fail
                    : element.Status == CheckStatus.Unknown && !element.OkInModel ? Adapter.ViewColor.ToCheck
                    : Adapter.ViewColor.Ok);
            }

            foreach (var orphan in result.Orphans) Mark(orphan.Element.Id, Adapter.ViewColor.Fail);
            return colors;
        }

        /// <summary>Element status for the screen: "ok in the model" when only exporter quantities are missing.</summary>
        public static string StatusText(ElementCheck element) =>
            element.FromIfc ? (element.Status == CheckStatus.Pass ? Tr.T("passa no IFC") : Tr.T("falha no IFC"))
            : element.OkInModel ? Tr.T("ok no modelo") : StatusText(element.Status);

        /// <summary>
        /// One-line reason: the failing requirements (or, without failures, the ones to check), with the exporter quantities
        /// grouped.
        /// </summary>
        public static string Reason(ElementCheck element)
        {
            var status = element.Status;
            if (status == CheckStatus.Pass) return "";
            var relevant = element.Requirements.Where(r => r.Status == status).ToList();
            var parts = new List<string>();
            var own = relevant.Where(r => !r.ByExporter).ToList();
            if (own.Count > 0)
            {
                parts.Add(own[0].ToString() + (own.Count > 1 ? " (+" + (own.Count - 1) + ")" : ""));
            }

            int quantities = relevant.Count(r => r.ByExporter);
            if (quantities > 0) parts.Add(quantities + Tr.T(" quantidade(s) calculada(s) pelo exportador"));
            return string.Join("; ", parts);
        }

        public static string StatusText(CheckStatus status) => status switch
        {
            CheckStatus.Pass => Tr.T("passa"),
            CheckStatus.Fail => Tr.T("falha"),
            _ => Tr.T("a conferir"),
        };

        /// <summary>
        /// Result per specification and element as JSON (exporter GlobalId), to compare with the IFC exported outside Revit
        /// (<c>tools/oracle/compare_prevalidation.py</c>).
        /// </summary>
        public static string ToJson(PrevalidationResult result)
        {
            var json = new StringBuilder("{\"elements\":[");
            json.Append(string.Join(",", result.Model.Elements
                .Select(JsonId).Where(g => g != null).Select(g => Quote(g!))));
            json.Append("],\n\"checks\":[");
            bool first = true;
            foreach (var specification in result.Specifications)
            {
                foreach (var element in specification.Elements)
                {
                    json.Append(first ? "\n" : ",\n");
                    first = false;
                    json.Append("{\"spec\":").Append(Quote(specification.Specification.Name))
                        .Append(",\"globalId\":").Append(Quote(JsonId(element.Element) ?? ""))
                        .Append(",\"revitId\":").Append(Quote(element.Element.Id))
                        .Append(",\"status\":").Append(Quote(element.Status.ToString().ToLowerInvariant()))
                        .Append(",\"checks\":[")
                        .Append(string.Join(",", element.Requirements.Where(r => r.Status != CheckStatus.Pass)
                            .Select(r => Quote(StatusText(r.Status) + " · " + r))))
                        .Append("]}");
                }
            }

            return json.Append("\n]}\n").ToString();
        }

        /// <summary>GlobalId, or <c>entity:IFCBUILDING</c> for the project-level entities (one per IFC, linked by entity).</summary>
        private static string? JsonId(Rules.ElementState element) =>
            Adapter.ModelReader.ProjectLevelEntities.Contains(element.Entity) ? "entity:" + element.Entity : element.GlobalId;

        private static string Quote(string text)
        {
            var quoted = new StringBuilder("\"");
            foreach (char c in text)
            {
                switch (c)
                {
                    case '"': quoted.Append("\\\""); break;
                    case '\\': quoted.Append("\\\\"); break;
                    case '\n': quoted.Append("\\n"); break;
                    default:
                        if (c < ' ') quoted.Append("\\u").Append(((int)c).ToString("x4"));
                        else quoted.Append(c);
                        break;
                }
            }

            return quoted.Append('"').ToString();
        }
    }
}
