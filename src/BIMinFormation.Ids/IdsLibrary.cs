using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BIMinFormation.Ids
{
    /// <summary>
    /// Set of loaded <c>.ids</c> files (a folder, read recursively, or a list of files). The specifications are kept in
    /// file order.
    /// </summary>
    public sealed class IdsLibrary
    {
        public IReadOnlyList<IdsDocument> Documents { get; }

        public IReadOnlyList<Specification> Specifications { get; }

        public IReadOnlyList<IdsDiagnostic> Diagnostics { get; }

        private IdsLibrary(IReadOnlyList<IdsDocument> documents, IReadOnlyList<IdsDiagnostic> diagnostics)
        {
            Documents = documents;
            Specifications = documents.SelectMany(d => d.Specifications).ToList();
            Diagnostics = diagnostics;
        }

        public static IdsLibrary LoadFolder(string folder, bool recursive = true)
        {
            if (!Directory.Exists(folder))
            {
                throw new DirectoryNotFoundException(Tr.T("Pasta não encontrada: ") + folder);
            }

            IEnumerable<string> files = Directory
                .EnumerateFiles(folder, "*.ids", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
            return LoadFiles(files);
        }

        public static IdsLibrary LoadFiles(IEnumerable<string> files)
        {
            var documents = new List<IdsDocument>();
            var diagnostics = new List<IdsDiagnostic>();

            foreach (string file in files)
            {
                IdsReadResult result = IdsParser.ReadFile(file);
                diagnostics.AddRange(result.Diagnostics);
                if (result.Document != null)
                {
                    documents.Add(result.Document);
                }
            }

            diagnostics.AddRange(FindDuplicates(documents));
            return new IdsLibrary(documents, diagnostics);
        }

        /// <summary>
        /// The same specification (name) in different files, for example the 5 Luxembourg phases loaded together: becomes a
        /// warning, because the requirements would add up.
        /// </summary>
        private static IEnumerable<IdsDiagnostic> FindDuplicates(IEnumerable<IdsDocument> documents)
        {
            IEnumerable<IGrouping<string, Specification>> groups = documents
                .SelectMany(d => d.Specifications)
                .Where(s => s.Name.Length > 0)
                .GroupBy(s => s.Name, StringComparer.Ordinal);

            foreach (IGrouping<string, Specification> group in groups)
            {
                List<string?> files = group.Select(s => s.SourceFile).Distinct().ToList();
                if (files.Count > 1)
                {
                    yield return new IdsDiagnostic(DiagnosticSeverity.Warning, DiagnosticCodes.DuplicateSpecification,
                        Tr.T("A ficha aparece em ") + files.Count + Tr.T(" arquivos: ") +
                        string.Join(", ", files.Select(f => f == null ? "?" : Path.GetFileName(f))) + ".",
                        files[0], 0, group.Key);
                }
            }
        }
    }
}
