using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Form = System.Windows.Forms.Form;
using Timer = System.Windows.Forms.Timer;
using Color = System.Drawing.Color;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;
using BIMinFormation.Core.Classification;
using BIMinFormation.Revit.Services;
using BIMinFormation.Ids;

namespace BIMinFormation.Revit.UI
{
    /// <summary>Classification window: modeless, one row per modeled element.</summary>
    internal sealed class ClassificationForm : SyncedForm
    {
        private const int ElementColumn = 0;
        private const int SpecColumn = 1;
        private const int ExportAsColumn = 2;

        private readonly DataGridView _grid = UiKit.Grid();
        private readonly Label _summary = new Label();
        private readonly Button _classifySelection;
        private readonly Button _apply;
        private List<ClassificationRow> _rows = new List<ClassificationRow>();
        private int _sortColumn = -1;
        private bool _ascending = true;
        private bool _filling;

        public static ClassificationForm? Current { get; private set; }

        /// <summary>For the tests inside Revit.</summary>
        internal int GridRowCount => _grid.Rows.Count;

        internal string SummaryText => _summary.Text;

        public ClassificationForm(UIApplication application, Document document, RevitQueue queue, PluginContext context, List<ClassificationRow> rows)
            : base(application, document, queue, context)
        {
            UiKit.Setup(this, Tr.T("BIMinFormation — Classificação — ") + Document.Title, new Size(1100, 640));
            StartPosition = FormStartPosition.CenterScreen;

            _grid.Columns.Add(UiKit.TextColumn(Tr.T("ELEMENTO"), 40));
            _grid.Columns.Add(UiKit.TextColumn(Tr.T("ESPECIFICAÇÃO DO IDS"), 35));
            _grid.Columns.Add(UiKit.TextColumn("IFCEXPORTAS", 25));
            _grid.Columns[SpecColumn].DefaultCellStyle.ForeColor = Color.FromArgb(0, 84, 147);

            _classifySelection = UiKit.Button(Tr.T("Classificar seleção"), (s, e) => Pick(SelectedRows()), 160);
            _apply = UiKit.Button(Tr.T("Aplicar"), (s, e) => Apply());
            var close = UiKit.Button(Tr.T("Fechar"), (s, e) => Close());
            Controls.Add(_grid);
            Controls.Add(UiKit.Footer(_summary, _classifySelection, _apply, close));

            _grid.ColumnHeaderMouseClick += (s, e) => Sort(e.ColumnIndex);
            _grid.CellClick += OnCellClick;
            _grid.CellDoubleClick += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.ColumnIndex == ElementColumn) ShowInRevit();
            };
            _grid.SelectionChanged += (s, e) =>
            {
                if (_filling) return;
                UpdateSummary();
                SendSelection();
            };
            FormClosing += OnClosing;
            FormClosed += (s, e) => Current = null;
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) Close();
            };

            Current = this;
            SetRows(rows);
        }

        protected override void Rebuild(UIApplication application)
        {
            Context = PluginContext.Create(Document, out _) ?? Context;
            var fresh = ClassificationService.BuildRows(Document, Context);
            var pending = _rows.Where(r => r.Chosen != null).ToDictionary(r => r.Id.IdValue(), r => r.Chosen);
            foreach (var row in fresh)
            {
                if (pending.TryGetValue(row.Id.IdValue(), out var chosen))
                {
                    row.Chosen = chosen; // choices not applied yet survive the refresh
                }
            }

            SetRows(fresh);
        }

        private void SetRows(List<ClassificationRow> rows)
        {
            long? anchor = _grid.FirstDisplayedScrollingRowIndex >= 0 && _grid.FirstDisplayedScrollingRowIndex < _rows.Count
                ? DisplayedRow(_grid.FirstDisplayedScrollingRowIndex)?.Id.IdValue()
                : null;
            var selected = new HashSet<long>(SelectedRows().Select(r => r.Id.IdValue()));
            _rows = rows;
            Fill(selected, anchor);
        }

        private IEnumerable<ClassificationRow> Ordered()
        {
            if (_sortColumn < 0)
            {
                return _rows.OrderBy(r => r.Order);
            }

            Func<ClassificationRow, string> key = _sortColumn switch
            {
                ElementColumn => r => r.ElementText,
                SpecColumn => r => r.SpecText,
                _ => r => r.ExportAsText,
            };
            var comparer = PortugueseComparer.Instance;
            // ascending: rows without a specification first; ties follow the model order
            return _ascending
                ? _rows.OrderBy(r => r.HasSpec ? 1 : 0).ThenBy(key, comparer).ThenBy(r => r.Order)
                : _rows.OrderByDescending(key, comparer).ThenBy(r => r.Order);
        }

        private void Fill(ISet<long> selected, long? anchor)
        {
            _filling = true;
            try
            {
                _grid.Rows.Clear();
                foreach (var row in Ordered())
                {
                    int index = _grid.Rows.Add(row.ElementText, row.SpecText, row.ExportAsText);
                    var gridRow = _grid.Rows[index];
                    gridRow.Tag = row;
                    gridRow.Cells[ElementColumn].ToolTipText = row.ToolTip;
                    gridRow.Cells[SpecColumn].ToolTipText = row.ToolTip;
                    if (row.Chosen != null)
                    {
                        gridRow.DefaultCellStyle.BackColor = Color.FromArgb(255, 248, 220);
                    }

                    if (row.Recognition?.Status == RecognitionStatus.OrphanIdentity)
                    {
                        gridRow.Cells[SpecColumn].Style.ForeColor = UiKit.Pending;
                    }

                    gridRow.Selected = selected.Contains(row.Id.IdValue());
                }

                if (anchor != null)
                {
                    var target = _grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => ((ClassificationRow)r.Tag!).Id.IdValue() == anchor);
                    if (target != null) _grid.FirstDisplayedScrollingRowIndex = target.Index;
                }

                UiKit.ShowSort(_grid, _sortColumn, _ascending);
            }
            finally
            {
                _filling = false;
            }

            UpdateSummary();
        }

        private ClassificationRow? DisplayedRow(int index) =>
            index >= 0 && index < _grid.Rows.Count ? _grid.Rows[index].Tag as ClassificationRow : null;

        private List<ClassificationRow> SelectedRows() =>
            _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => (ClassificationRow)r.Tag!).ToList();

        private void UpdateSummary()
        {
            int selected = _grid.SelectedRows.Count;
            int toApply = _rows.Count(r => r.Chosen != null);
            _summary.Text = Tr.T("Elementos: ") + _rows.Count + Tr.T(" · Fichas: ") + Context.Library.Specifications.Count
                            + Tr.T(" · Com ficha: ") + _rows.Count(r => r.HasSpec) + Tr.T(" · A aplicar: ") + toApply
                            + Tr.T(" · Selecionados: ") + selected;
            _classifySelection.Text = Tr.T("Classificar seleção (") + selected + ")";
            _classifySelection.Enabled = selected >= 2;
            _apply.Enabled = toApply > 0 && !Writing;
        }

        private void Sort(int column)
        {
            _ascending = _sortColumn != column || !_ascending;
            _sortColumn = column;
            Fill(new HashSet<long>(SelectedRows().Select(r => r.Id.IdValue())), null);
        }

        private void OnCellClick(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != SpecColumn || (ModifierKeys & Keys.Control) != 0)
            {
                return;
            }

            var clicked = (ClassificationRow)_grid.Rows[e.RowIndex].Tag!;
            var selection = SelectedRows();
            // clicking a row outside the selection applies only to that row
            Pick(selection.Count > 1 && selection.Contains(clicked) ? selection : new List<ClassificationRow> { clicked });
        }

        private void Pick(List<ClassificationRow> rows)
        {
            if (rows.Count == 0)
            {
                return;
            }

            var current = rows[0].Chosen?.Specification
                          ?? (rows[0].Recognition?.Specifications.Count == 1 ? rows[0].Recognition!.Specifications[0] : null);
            using var picker = new SpecPickerForm(Context.Library.Specifications, Context.Schema, rows.Count, current);
            if (picker.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            foreach (var row in rows)
            {
                row.Chosen = picker.Cleared ? null : picker.Result;
            }

            Fill(new HashSet<long>(rows.Select(r => r.Id.IdValue())), null);
        }

        private void Apply()
        {
            if (_rows.All(r => r.Chosen == null))
            {
                return;
            }

            Writing = true;
            _apply.Enabled = false;
            Queue.Enqueue(Tr.T("aplicar a Classificação"), app =>
            {
                try
                {
                    var summary = ClassificationService.Apply(Document, Context, _rows, ParametersFile);
                    Log.Info("Classification: " + summary.ToString().Replace("\n", " "));
                    var failed = new HashSet<string>(summary.Failures);
                    foreach (var row in _rows.Where(r => r.Chosen != null))
                    {
                        if (!summary.Failures.Any(f => f.StartsWith(row.ElementText, StringComparison.Ordinal)))
                        {
                            row.Chosen = null;
                        }
                    }

                    Rebuild(app);
                    TaskDialog.Show(Tr.T("BIMinFormation — Classificação"), summary.ToString());
                }
                finally
                {
                    Writing = false;
                    UpdateSummary();
                }
            });
        }

        private void OnClosing(object? sender, FormClosingEventArgs e)
        {
            if (_rows.Any(r => r.Chosen != null)
                && MessageBox.Show(this, Tr.T("Há fichas escolhidas e não aplicadas. Fechar descarta essas escolhas. Continuar?"),
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                e.Cancel = true;
            }
        }

        protected override IReadOnlyCollection<ElementId> SelectedIds() => SelectedRows().Select(r => r.Id).ToList();

        protected override void SelectInGrid(ISet<long> ids)
        {
            _filling = true;
            try
            {
                DataGridViewRow? first = null;
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    bool select = ids.Contains(((ClassificationRow)row.Tag!).Id.IdValue());
                    row.Selected = select;
                    if (select && first == null) first = row;
                }

                if (first != null && !first.Displayed) _grid.FirstDisplayedScrollingRowIndex = first.Index;
            }
            finally
            {
                _filling = false;
            }

            UpdateSummary();
        }
    }
}
