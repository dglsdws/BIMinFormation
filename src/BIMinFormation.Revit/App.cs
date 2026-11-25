using System;
using System.Collections.Generic;
using System.Reflection;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.UI;
using BIMinFormation.Revit.Commands;
using BIMinFormation.Ids;

namespace BIMinFormation.Revit
{
    /// <summary>Creates the BIMinFormation ribbon tab with its panel.</summary>
    public sealed class App : IExternalApplication
    {
        public const string TabName = "BIMinFormation";
        /// <summary>Internal name of the only panel; its title on the ribbon stays empty (a single group needs no name).</summary>
        public const string PanelName = "BIMinFormation";

        public static string Version =>
            Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

        /// <summary>The add-in went through OnStartup (used by the tests inside Revit).</summary>
        public static bool Loaded { get; private set; }

        /// <summary>Language of the Revit in use (for the "automatic" choice).</summary>
        public static LanguageType RevitLanguage { get; private set; } = LanguageType.Brazilian_Portuguese;

        private static RibbonPanel? _panel;
        private static readonly List<(PushButton Button, string Text, string ToolTip)> Buttons = new List<(PushButton, string, string)>();

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                RevitLanguage = application.ControlledApplication.Language;
                ApplyLanguage(PluginSettings.Load().Language);
                application.CreateRibbonTab(TabName);
                _panel = application.CreateRibbonPanel(TabName, PanelName);
                try
                {
                    _panel.Title = "";
                }
                catch (Exception ex)
                {
                    Log.Info("Ribbon: panel without title refused (" + ex.Message + "); keeps " + PanelName);
                }

                string assembly = Assembly.GetExecutingAssembly().Location;

                // texts in Portuguese; the translation happens in Tr.T (and again when the language changes in Settings)
                AddButton<ClassificationCommand>(_panel, assembly, Tr.N("Classificação"), "classificacao",
                    Tr.N("Escolhe, para cada elemento, a ficha do .ids que vale para ele."));
                AddButton<FillCommand>(_panel, assembly, Tr.N("Preencher\nParâmetros"), "preencher",
                    Tr.N("Mostra o que a ficha exige e grava os valores nos parâmetros do Revit."));
                AddButton<PrevalidationCommand>(_panel, assembly, Tr.N("Pré-\nvalidação"), "prevalidacao",
                    Tr.N("Confere as fichas no modelo antes de exportar: o que passa, o que falha e por quê."));
                AddButton<ExportCommand>(_panel, assembly, Tr.N("Exportar\nIFC"), "exportar",
                    Tr.N("Abre a exportação IFC do Revit com a configuração que as fichas pedem (versão, quantidades e conjuntos). Exporte sempre por este botão: ele refaz o mapeamento do projeto atual antes de abrir a janela e avisa o que não vai passar."));
                AddButton<SettingsCommand>(_panel, assembly, Tr.N("Configurações"), "configuracoes",
                    Tr.N("Escolhe a pasta dos .ids e mostra o que foi encontrado. Também gera o arquivo de PropertySets avulso e limpa parâmetros."));
                AddButton<AboutCommand>(_panel, assembly, Tr.N("Sobre"), "sobre", Tr.N("Versão, autoria e licença do BIMinFormation."));
#if REVIT2025_OR_GREATER
                application.ThemeChanged += (sender, e) => UI.RibbonIcons.Refresh(); // Revit light/dark theme
#endif

                Adapter.ModelCache.Subscribe(application.ControlledApplication);
                try
                {
                    Services.PaneController.Register(application);
                }
                catch (Exception ex)
                {
                    Log.Error("Conformity panel not registered.", ex);
                }

                Log.Info("BIMinFormation " + Version + " loaded (language " + Tr.Language + ").");
                Loaded = true;
                StartTestHarness(application);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Error("Could not create the ribbon.", ex);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

        /// <summary>Language of the choice (<c>auto</c> = Revit's: Brazilian Portuguese stays Portuguese; the others, English).</summary>
        public static string ResolveLanguage(string choice) =>
            choice == Tr.Portuguese || choice == Tr.English
                ? choice
                : RevitLanguage == LanguageType.Brazilian_Portuguese ? Tr.Portuguese : Tr.English;

        /// <summary>Applies the language and updates the ribbon texts (windows opened afterwards already use the new language).</summary>
        public static void ApplyLanguage(string choice)
        {
            Tr.Language = ResolveLanguage(choice);
            foreach (var (button, text, toolTip) in Buttons)
            {
                button.ItemText = Tr.T(text);
                button.ToolTip = Tr.T(toolTip);
            }
        }

        /// <summary>
        /// Debug build only: if BIMINFORMATION_REVIT_TESTS points to the DLL of the tests inside Revit, loads and starts it
        /// (tools/revit-test.ps1). The Release build does not have this hook.
        /// </summary>
        [System.Diagnostics.Conditional("DEBUG")]
        private static void StartTestHarness(UIControlledApplication application)
        {
            string? harness = Environment.GetEnvironmentVariable("BIMINFORMATION_REVIT_TESTS");
            if (string.IsNullOrEmpty(harness) || !System.IO.File.Exists(harness))
            {
                return;
            }

            try
            {
                Type? type = Assembly.LoadFrom(harness).GetType("BIMinFormation.Revit.Harness.Harness", true);
                type!.GetMethod("Start")!.Invoke(null, new object[] { application });
                Log.Info("Tests inside Revit started: " + harness);
            }
            catch (Exception ex)
            {
                Log.Error("Could not start the tests inside Revit.", ex);
            }
        }

        private static void AddButton<TCommand>(RibbonPanel panel, string assembly, string text, string icon, string tooltip)
        {
            Type type = typeof(TCommand);
            var data = new PushButtonData(type.Name, Tr.T(text), assembly, type.FullName) { ToolTip = Tr.T(tooltip) };
            if (panel.AddItem(data) is PushButton button)
            {
                Buttons.Add((button, text, tooltip));
                try
                {
                    UI.RibbonIcons.Set(button, icon);
                }
                catch (Exception ex)
                {
                    Log.Error("Icon of button " + type.Name + " not applied.", ex); // the button works without an icon
                }
            }
        }
    }
}
