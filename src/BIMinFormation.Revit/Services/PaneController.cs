using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BIMinFormation.Revit.UI;
using BIMinFormation.Ids;

namespace BIMinFormation.Revit.Services
{
    /// <summary>
    /// Conformity panel: registered in OnStartup; refreshed in Revit Idling (API context) a few seconds after the last
    /// change, only while the panel is visible. The panel buttons become requests handled in Idling.
    /// </summary>
    public static class PaneController
    {
        public static readonly DockablePaneId Id = new DockablePaneId(new Guid("6b2d4b1e-3f5a-4c8e-9d71-2a6f0c3e8b45"));

        private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2);
        private static ConformityPane? _pane;
        private static bool _dirty = true;
        private static DateTime _changed = DateTime.MinValue;
        private static bool _openRequested;
        private static string? _openSpec;

        internal static ConformityPane? Pane => _pane;

        public static void Register(UIControlledApplication application)
        {
            _pane = new ConformityPane();
            application.RegisterDockablePane(Id, Tr.T("BIMinFormation — Conformidade"), _pane);
            _pane.OpenRequested += spec =>
            {
                _openSpec = spec;
                _openRequested = true;
            };
            application.Idling += OnIdling;
            application.ViewActivated += (s, e) =>
            {
                if (e.PreviousActiveView?.Document?.Equals(e.CurrentActiveView?.Document) != true) Touch(immediately: true);
            };
            application.ControlledApplication.DocumentChanged += (s, e) => Touch(immediately: false);
        }

        /// <summary>Shows or hides the panel (Panel button of the pre-validation); returns whether it ended up visible.</summary>
        public static bool Toggle(UIApplication application)
        {
            var pane = application.GetDockablePane(Id);
            if (pane.IsShown())
            {
                pane.Hide();
                return false;
            }

            pane.Show();
            Touch(immediately: true);
            return true;
        }

        /// <summary>Marks for refresh (now, or after a few seconds without changes).</summary>
        public static void Touch(bool immediately)
        {
            _dirty = true;
            _changed = immediately ? DateTime.MinValue : DateTime.Now;
        }

        private static void OnIdling(object? sender, IdlingEventArgs e)
        {
            if (!(sender is UIApplication application) || _pane == null) return;
            try
            {
                if (_openRequested)
                {
                    _openRequested = false;
                    OpenPrevalidation(application, _openSpec);
                }

                if (!_dirty || DateTime.Now - _changed < Quiet) return;
                if (!DockablePane.PaneIsRegistered(Id)) return;
                DockablePane pane;
                try
                {
                    pane = application.GetDockablePane(Id);
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException)
                {
                    return; // registered, but Revit creates the pane only after the main window: next Idling (logged as an error before)
                }

                if (!pane.IsShown()) return;
                _dirty = false;
                Refresh(application.ActiveUIDocument?.Document);
            }
            catch (Exception ex)
            {
                _dirty = false;
                Log.Error("Conformity panel", ex);
            }
        }

        /// <summary>Recomputes the panel for the document (tests call it directly).</summary>
        public static void Refresh(Document? document)
        {
            if (_pane == null) return;
            if (document == null || document.IsFamilyDocument)
            {
                _pane.Show("BIMinFormation", null, Tr.T("Abra um projeto com o perfil configurado."));
                return;
            }

            var context = PluginContext.Create(document, out string? problem);
            if (context == null)
            {
                _pane.Show(document.Title, null, problem);
                return;
            }

            _pane.Show(document.Title, PrevalidationService.Run(document, context), null);
        }

        private static void OpenPrevalidation(UIApplication application, string? spec)
        {
            var document = application.ActiveUIDocument?.Document;
            if (document == null) return;
            if (PrevalidationForm.Current == null)
            {
                var context = Commands.Commanding.Context(application);
                if (context == null) return;
                new PrevalidationForm(application, document, Commands.Commanding.Queue, context, PrevalidationService.Run(document, context)).ShowNonModal();
            }

            PrevalidationForm.Current!.Activate();
            if (spec != null) PrevalidationForm.Current.SelectSpecification(spec);
        }
    }
}
