using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using BIMinFormation.Exporter;
using BIMinFormation.Ids;

namespace BIMinFormation.Revit.Adapter
{
    /// <summary>
    /// Catalog of the IFC exporter of the installation in use, read at run time (IFC versions in the API, PropertySets
    /// file types, standard Pset parameters). This way the plugin knows, for example, that Revit 2023.1 exports IFC 4.3 and
    /// 2023.0 does not. If reading fails, it uses the embedded catalog of the same year (or the newest).
    /// </summary>
    public static class RuntimeExporter
    {
        private static ExporterCatalog? _current;

        public static string Source { get; private set; } = "";

        public static ExporterCatalog Current => _current ??= Load();

        private static ExporterCatalog Load()
        {
            string year = typeof(Document).Assembly.GetName().Version is { } v && v.Major >= 20 ? (2000 + v.Major).ToString() : "2023";
            try
            {
                string revitFolder = InstalledParameters.RevitFolder;
                string exporterDll = ExporterDll(revitFolder, year);
                string version = FileVersionInfo.GetVersionInfo(exporterDll).FileVersion ?? "?";
                var propertyTypes = PropertyTypes(exporterDll);
                var lines = ExporterCatalogBuilder.Lines(year, version, Enum.GetNames(typeof(IFCVersion)), propertyTypes, revitFolder);
                using var reader = new StringReader(ExporterCatalogBuilder.Text(lines));
                var catalog = ExporterCatalog.Parse(reader);
                Source = Tr.T("instalação (") + exporterDll + ")";
                Log.Info("IFC exporter read from the installation: " + version + "; IFC versions: " + string.Join(", ", catalog.IfcVersions)
                         + "; " + catalog.PropertyTypes.Count + " types; " + catalog.StandardParameters.Count() + " standard parameters.");
                return catalog;
            }
            catch (Exception ex)
            {
                int embedded = ExporterCatalog.AvailableYears.Contains(int.Parse(year)) ? int.Parse(year) : ExporterCatalog.AvailableYears.Max();
                Log.Error("Could not read the installed IFC exporter; using the embedded catalog of Revit " + embedded + ".", ex);
                Source = Tr.T("catálogo embutido (Revit ") + embedded + ")";
                return ExporterCatalog.For(embedded);
            }
        }

        /// <summary>The updated exporter (IFC for Revit, in ApplicationPlugins) takes priority over the one shipped with Revit.</summary>
        private static string ExporterDll(string revitFolder, string year)
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Revit.IFC.Export");
            if (loaded != null && !string.IsNullOrEmpty(loaded.Location))
            {
                return loaded.Location;
            }

            string bundle = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Autodesk", "ApplicationPlugins", "IFC " + year + ".bundle", "Contents", year, "Revit.IFC.Export.dll");
            return File.Exists(bundle) ? bundle : Path.Combine(revitFolder, "Revit.IFC.Export.dll");
        }

        private static string[] PropertyTypes(string exporterDll)
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Revit.IFC.Export")
                           ?? Assembly.LoadFrom(exporterDll);
            var type = assembly.GetType("Revit.IFC.Export.Exporter.PropertySet.PropertyType", true)!;
            return Enum.GetNames(type);
        }
    }
}
