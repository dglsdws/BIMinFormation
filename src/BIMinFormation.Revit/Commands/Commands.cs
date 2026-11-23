using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;
using BIMinFormation.Core.Mapping;
using BIMinFormation.Core.Profile;
using BIMinFormation.Exporter;
using BIMinFormation.Ids;
using BIMinFormation.Revit.Adapter;
using BIMinFormation.Revit.Services;
using BIMinFormation.Revit.UI;

namespace BIMinFormation.Revit.Commands
{
    internal static class Dialogs
    {
        public const string Title = "BIMinFormation";

        public static void Show(string title, string main, string? content = null)
        {
            var dialog = new TaskDialog(title) { MainInstruction = main };
            if (content != null)
            {
                dialog.MainContent = content;
            }

            dialog.Show();
        }
    }

    /// <summary>Parts shared by the commands: Revit queue and context (asks for the folder the first time).</summary>
    internal static class Commanding
    {
        private static RevitQueue? _queue;

        /// <summary>The queue must be created in a valid API context: the first command creates it.</summary>
        public static RevitQueue Queue => _queue ??= new RevitQueue();

        public static PluginContext? Context(UIApplication application)
        {
            var document = application.ActiveUIDocument?.Document;
            if (document == null || document.IsFamilyDocument)
            {
                Dialogs.Show(Dialogs.Title, Tr.T("Abra um projeto do Revit."));
                return null;
            }

            var context = PluginContext.Create(document, out string? problem);
            if (context != null)
            {
                return context;
            }

            var dialog = new TaskDialog(Dialogs.Title)
            {
                MainInstruction = Tr.T("Configure os .ids do projeto"),
                MainContent = problem,
                CommonButtons = TaskDialogCommonButtons.Cancel,
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, Tr.T("Abrir as Configurações"));
            if (dialog.Show() != TaskDialogResult.CommandLink1 || !SettingsCommand.Edit(application))
            {
                return null;
            }

            context = PluginContext.Create(document, out problem);
            if (context == null)
            {
                Dialogs.Show(Dialogs.Title, Tr.T("Ainda falta configurar os .ids"), problem);
            }

            return context;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class ClassificationCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                if (ClassificationForm.Current != null)
                {
                    ClassificationForm.Current.Activate();
                    return Result.Succeeded;
                }

                var context = Commanding.Context(commandData.Application);
                if (context == null) return Result.Cancelled;
                var rows = ClassificationService.BuildRows(commandData.Application.ActiveUIDocument.Document, context);
                new ClassificationForm(commandData.Application, commandData.Application.ActiveUIDocument.Document, Commanding.Queue, context, rows).ShowNonModal();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Error("Classification", ex);
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class FillCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                if (FillForm.Current != null)
                {
                    FillForm.Current.Activate();
                    return Result.Succeeded;
                }

                var context = Commanding.Context(commandData.Application);
                if (context == null) return Result.Cancelled;
                var rows = FillService.BuildRows(commandData.Application.ActiveUIDocument.Document, context);
                new FillForm(commandData.Application, commandData.Application.ActiveUIDocument.Document, Commanding.Queue, context, rows).ShowNonModal();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Error("Fill In Parameters", ex);
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    /// <summary>Pre-validation: the specifications checked on the model before exporting, in a modeless window.</summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class PrevalidationCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                if (PrevalidationForm.Current != null)
                {
                    PrevalidationForm.Current.Activate();
                    PrevalidationForm.Current.RequestRebuild();
                    return Result.Succeeded;
                }

                var context = Commanding.Context(commandData.Application);
                if (context == null) return Result.Cancelled;
                var document = commandData.Application.ActiveUIDocument.Document;
                var result = PrevalidationService.Run(document, context);
                new PrevalidationForm(commandData.Application, document, Commanding.Queue, context, result).ShowNonModal();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Error("Pre-validation", ex);
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    /// <summary>
    /// Export: saves in the project the "BIMinFormation (IDS)" configuration built from the specifications (IFC version,
    /// quantities, standard sets and PropertySets file) and opens the Revit IFC export window with it. Revit does the export.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class ExportCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var context = Commanding.Context(commandData.Application);
                if (context == null) return Result.Cancelled;

                var document = commandData.Application.ActiveUIDocument.Document;
                var setup = ExportService.Prepare(document, context);
                if (!setup.CanExport)
                {
                    Dialogs.Show(Dialogs.Title, Tr.T("Este Revit não exporta o que as fichas pedem"), string.Join("\n", setup.Problems));
                    return Result.Cancelled;
                }

                if (setup.MappingWarnings.Count > 0)
                {
                    // what the mapping cannot meet, shown on every export
                    var text = new StringBuilder();
                    foreach (string warning in setup.MappingWarnings.Take(8)) text.AppendLine("• " + warning);
                    if (setup.MappingWarnings.Count > 8) text.AppendLine(Tr.T("… e mais ") + (setup.MappingWarnings.Count - 8) + ".");
                    text.AppendLine();
                    text.Append(Tr.T("A janela de exportação abre em seguida."));
                    Dialogs.Show(Dialogs.Title + Tr.T(" — Exportar IFC"), setup.MappingWarnings.Count + Tr.T(" aviso(s) do mapeamento"), text.ToString());
                }

                if (!ExportConfiguration.OpenDialog(commandData.Application, setup.Name))
                {
                    Dialogs.Show(Dialogs.Title, Tr.T("Escolha a configuração \"") + setup.Name + "\"",
                        Tr.T("A configuração foi gravada no projeto. Na janela de exportação, selecione-a na lista de configurações."));
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Error("Export", ex);
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    /// <summary>
    /// Standalone PropertySets file, from the Settings button: for exporting outside the Export button (in batch, by
    /// script, on another machine). Shows the mapping warnings and the export options. Does not change the document.
    /// </summary>
    internal static class PropertySetsFileCommand
    {
        public static void Save(UIApplication application)
        {
            try
            {
                var context = Commanding.Context(application);
                if (context == null) return;

                var document = application.ActiveUIDocument.Document;
                context = SpecificationUse.Refresh(document, context); // only the specifications in use in the model
                var specifications = context.Profile.MappedSpecifications(context.Library.Specifications);
                var file = PropertySetsFile.From(context.Mapping, context.SchemaCatalog, p => ExportService.ActualName(document, p));
                if (file.Sets.Count == 0)
                {
                    Dialogs.Show(Dialogs.Title, Tr.T("Nada a mapear"), Tr.T("As fichas só pedem Psets e Qtos padrão: não há conjunto do usuário."));
                    return;
                }

                string path;
                using (var picker = new SaveFileDialog
                       {
                           Title = Tr.T("Salvar o arquivo de PropertySets"),
                           Filter = Tr.T("PropertySets do usuário (*.txt)|*.txt"),
                           FileName = context.Profile.PropertySetsFile != null ? Path.GetFileName(context.Profile.PropertySetsFile) : "BIMinFormation_PropertySets.txt",
                           InitialDirectory = context.Profile.PropertySetsFile != null
                               ? Path.GetDirectoryName(context.Profile.PropertySetsFile)
                               : context.Profile.IdsFolder ?? "",
                       })
                {
                    if (picker.ShowDialog(new RevitWindow(application.MainWindowHandle)) != DialogResult.OK)
                    {
                        return;
                    }

                    path = picker.FileName;
                }

                File.WriteAllBytes(path, file.ToBytes(ExportService.PropertySetsHeader(context, specifications.Count)));

                var profile = context.Profile;
                profile.PropertySetsFile = path;
                var checklist = ExportChecklist.Build(specifications, context.Schema, context.Exporter, profile, context.Format);
                var text = new StringBuilder();
                text.AppendLine(path);
                text.AppendLine(Tr.T("Formato: ") + MappingFormats.Describe(context.Format));
                text.AppendLine(file.Sets.Count + Tr.T(" conjunto(s), ") + file.Sets.Sum(s => s.Properties.Count) + Tr.T(" propriedade(s)."));
                if (file.Warnings.Count > 0)
                {
                    text.AppendLine();
                    text.AppendLine(file.Warnings.Count + Tr.T(" aviso(s):"));
                    foreach (string warning in file.Warnings.Take(8)) text.AppendLine("• " + warning);
                }

                text.AppendLine();
                text.AppendLine(Tr.T("Na exportação IFC:"));
                foreach (string item in checklist) text.AppendLine("• " + item);
                Log.Info("PropertySets file: " + path + " — " + file.Sets.Count + " sets, " + file.Warnings.Count + " warnings");
                Dialogs.Show(Dialogs.Title + Tr.T(" — PropertySets"), Tr.T("Arquivo de PropertySets gerado"), text.ToString());
            }
            catch (Exception ex)
            {
                Log.Error("PropertySets file", ex);
                Dialogs.Show(Dialogs.Title, Tr.T("Falha ao gerar o arquivo de PropertySets"), ex.Message);
            }
        }
    }

    /// <summary>Settings window; "Save to project" writes the profile to the .rvt (the file is not saved).</summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class SettingsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var document = commandData.Application.ActiveUIDocument?.Document;
                if (document == null || document.IsFamilyDocument)
                {
                    Dialogs.Show(Dialogs.Title, Tr.T("Abra um projeto do Revit."));
                    return Result.Cancelled;
                }

                return Edit(commandData.Application) ? Result.Succeeded : Result.Cancelled;
            }
            catch (Exception ex)
            {
                Log.Error("Settings", ex);
                message = ex.Message;
                return Result.Failed;
            }
        }

        /// <summary>Clean up plugin parameters: lists them, the user picks, deletes them in one transaction.</summary>
        internal static void Cleanup(UIApplication application)
        {
            var context = Commanding.Context(application);
            if (context == null) return;
            var document = application.ActiveUIDocument.Document;
            context = SpecificationUse.Refresh(document, context); // "in the mapping" = specifications in use now
            var candidates = ParameterCleanup.Find(document, context);
            using var form = new CleanupForm(candidates);
            if (form.ShowDialog(new RevitWindow(application.MainWindowHandle)) != DialogResult.OK) return;
            int deleted = ParameterCleanup.Delete(document, form.Chosen);
            Log.Info("Clean up parameters: " + deleted + " deleted");
            Dialogs.Show(Dialogs.Title, deleted + Tr.T(" parâmetro(s) apagado(s)"), Tr.T("O modelo não foi salvo."));
        }

        /// <summary>Opens the window and, if the user saves, writes the profile to the document and the folder to the preferences.</summary>
        internal static bool Edit(UIApplication application)
        {
            var document = application.ActiveUIDocument.Document;
            using var form = new SettingsForm(PluginContext.LoadProfile(document), RuntimeExporter.Current, PluginSettings.Load().Language);
            if (form.ShowDialog(new RevitWindow(application.MainWindowHandle)) != DialogResult.OK)
            {
                if (form.CleanupRequested) Cleanup(application);
                if (form.PropertySetsFileRequested) PropertySetsFileCommand.Save(application);
                return false;
            }

            using (var transaction = new Transaction(document, Tr.T("BIMinFormation: perfil do projeto")))
            {
                transaction.Start();
                ProfileStorage.Save(document, form.Profile);
                transaction.Commit();
            }

            var settings = PluginSettings.Load();
            settings.IdsFolder = form.Profile.IdsFolder;
            settings.Language = form.LanguageChoice;
            App.ApplyLanguage(settings.Language); // ribbon already in the new language; windows opened later too
            settings.Save();
            Log.Info("Project profile saved: " + form.Profile.ToJson());
            ClassificationForm.Current?.RequestRebuild();
            FillForm.Current?.RequestRebuild();
            return true;
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class AboutCommand : IExternalCommand
    {
        public const string Author = "Douglas W. Santos";
        public const string Email = "dglsdws@gmail.com";
        public const string Repository = "https://github.com/dglsdws/BIMinFormation";

        /// <summary>Window text: what the plugin does, authorship, license and where to find it.</summary>
        internal static string Text() =>
            Tr.T("Ajuda a preencher o modelo, desde a modelagem, com o que os .ids (buildingSMART IDS 1.0) exigem do IFC.\n\n")
            + "© 2026 " + Author + Tr.T(" — Licença MIT\n")
            + "GitHub: " + Repository + "\n"
            + "Email: " + Email;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Dialogs.Show(Dialogs.Title + Tr.T(" — Sobre"), "BIMinFormation " + App.Version, Text());
            return Result.Succeeded;
        }
    }
}
