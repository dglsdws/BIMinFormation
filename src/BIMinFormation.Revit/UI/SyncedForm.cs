using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Form = System.Windows.Forms.Form;
using Timer = System.Windows.Forms.Timer;
using Color = System.Drawing.Color;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BIMinFormation.Revit.Services;
using BIMinFormation.Ids;

namespace BIMinFormation.Revit.UI
{
    /// <summary>
    /// Base of the modeless windows: talks to Revit only through the <see cref="RevitQueue"/>, rebuilds the grid when the
    /// model changes (several changes in a row become a single rebuild) and syncs the selection both ways.
    /// </summary>
    internal abstract class SyncedForm : Form
    {
        private readonly Timer _debounce = new Timer { Interval = 600 };
        private readonly UIApplication _application;
        private bool _applyingRevitSelection;
        private bool _closing;

        protected RevitQueue Queue { get; }
        protected Document Document { get; }
        protected PluginContext Context { get; set; }

        /// <summary>The plugin itself is writing: model changes do not trigger an extra rebuild.</summary>
        protected bool Writing { get; set; }

        private bool _subscribed;

        protected SyncedForm(UIApplication application, Document document, RevitQueue queue, PluginContext context)
        {
            _application = application;
            Queue = queue;
            Document = document;
            Context = context;
            _debounce.Tick += (s, e) =>
            {
                _debounce.Stop();
                RequestRebuild();
            };
            application.Application.DocumentChanged += OnDocumentChanged;
            application.SelectionChanged += OnSelectionChanged;
            _subscribed = true;
            FormClosed += (s, e) =>
            {
                _closing = true;
                _debounce.Stop();
                Queue.Enqueue(Tr.T("fechar janela"), Unsubscribe);
            };
        }

        private void Unsubscribe(UIApplication application)
        {
            if (!_subscribed) return;
            application.Application.DocumentChanged -= OnDocumentChanged;
            application.SelectionChanged -= OnSelectionChanged;
            _subscribed = false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _closing = true;
                _debounce.Dispose();
                if (_subscribed)
                {
                    // disposed without being closed (tests): tries to unhook now, otherwise through the queue
                    try
                    {
                        Unsubscribe(_application);
                    }
                    catch (Exception)
                    {
                        Queue.Enqueue(Tr.T("fechar janela"), Unsubscribe);
                    }
                }
            }

            base.Dispose(disposing);
        }

        /// <summary>Reads the model again and fills the grid (called inside the API context).</summary>
        protected abstract void Rebuild(UIApplication application);

        /// <summary>Elements selected in the grid.</summary>
        protected abstract IReadOnlyCollection<ElementId> SelectedIds();

        /// <summary>Selects in the grid the elements selected in Revit.</summary>
        protected abstract void SelectInGrid(ISet<long> ids);

        public void RequestRebuild()
        {
            if (_closing)
            {
                return;
            }

            Queue.Enqueue(Tr.T("atualizar a grade"), app =>
            {
                if (!_closing && Document.IsValidObject)
                {
                    Rebuild(app);
                }
            });
        }

        private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
        {
            if (_closing || Writing || !e.GetDocument().Equals(Document))
            {
                return;
            }

            _debounce.Stop();
            _debounce.Start();
        }

        private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_closing || !e.GetDocument().Equals(Document) || _sendingSelection)
            {
                return;
            }

            _applyingRevitSelection = true;
            try
            {
                SelectInGrid(new HashSet<long>(e.GetSelectedElements().Select(id => id.IdValue())));
            }
            finally
            {
                _applyingRevitSelection = false;
            }
        }

        private bool _sendingSelection;

        /// <summary>Highlights in Revit the elements selected in the grid.</summary>
        protected void SendSelection()
        {
            if (_applyingRevitSelection || _closing)
            {
                return;
            }

            var ids = SelectedIds().ToList();
            Queue.Enqueue(Tr.T("selecionar no Revit"), app =>
            {
                var uiDocument = app.ActiveUIDocument;
                if (uiDocument == null || !uiDocument.Document.Equals(Document))
                {
                    return;
                }

                _sendingSelection = true;
                try
                {
                    uiDocument.Selection.SetElementIds(ids);
                }
                finally
                {
                    _sendingSelection = false;
                }
            });
        }

        /// <summary>Zooms the view to the elements (double-click).</summary>
        protected void ShowInRevit()
        {
            var ids = SelectedIds().ToList();
            if (ids.Count == 0)
            {
                return;
            }

            Queue.Enqueue(Tr.T("aproximar a vista"), app =>
            {
                var uiDocument = app.ActiveUIDocument;
                if (uiDocument != null && uiDocument.Document.Equals(Document))
                {
                    uiDocument.ShowElements(ids);
                }
            });
        }

        protected string ParametersFile =>
            string.IsNullOrEmpty(Context.Profile.ParametersFile) ? Adapter.ParameterWriter.DefaultParametersFile : Context.Profile.ParametersFile!;

        public void ShowNonModal()
        {
            Show(new RevitWindow(_application.MainWindowHandle));
        }
    }
}
