using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;
using BIMinFormation.Core.Classification;
using BIMinFormation.Core.Fill;
using BIMinFormation.Ids;
using BIMinFormation.Revit.Services;
using Color = System.Drawing.Color;
using ComboBox = System.Windows.Forms.ComboBox;
using TextBox = System.Windows.Forms.TextBox;
using Panel = System.Windows.Forms.Panel;

namespace BIMinFormation.Revit.UI
{
    /// <summary>
    /// Fill In Parameters window: modeless; elements with a specification on the left, the requirements of the selected
    /// one(s) on the right, only the VALUE column is editable, and Apply writes everything in a single transaction.
    /// </summary>
    internal sealed class FillForm : SyncedForm
    {
        private static string Empty => Tr.T("(vazio)");
        private static string AllSpecs => Tr.T("(todas)");
        private static string MixedText => Tr.T("(misto)"); // the same text as FillCell.Mixed, in the language in use
        private const int ValueColumn = 3;

        private readonly DataGridView _elements = UiKit.Grid();
        private readonly DataGridView _cells = UiKit.Grid();
        private readonly Label _summary = new Label { Dock = DockStyle.Top, Height = UiKit.Px(22), ForeColor = UiKit.Muted };
        private readonly Label _footer = new Label();
        private readonly TextBox _search = new TextBox { Width = UiKit.Px(260) };
        private readonly CheckBox _onlyPending = new CheckBox { Text = Tr.T("Só com pendências"), AutoSize = true, Padding = UiKit.Px(12, 3, 0, 0) };
        private SplitContainer? _split;

        /// <summary>Width of the two grids (tests).</summary>
        internal (int Elements, int Properties) GridWidths => (_split?.Panel1.Width ?? 0, _split?.Panel2.Width ?? 0);

        private readonly ComboBox _spec = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = UiKit.Px(300) };
        private readonly Button _apply;
        private readonly Button _link;
        private readonly Button _copy;
        private readonly Button _paste;
        private Dictionary<string, string>? _clipboard;
        private string _clipboardSource = "";

        /// <summary>Copied values (tests).</summary>
        internal int ClipboardCount => _clipboard?.Count ?? 0;
        private List<FillElementRow> _rows = new List<FillElementRow>();
        private int _sortColumn = -1;
        private bool _ascending = true;
        private bool _filling;

        public static FillForm? Current { get; private set; }

        /// <summary>For the tests inside Revit.</summary>
        internal int ElementRowCount => _elements.Rows.Count;

        internal int CellRowCount => _cells.Rows.Count;

        internal string SummaryText => _summary.Text;

        internal void SelectFirstElementWithSpec()
        {
            foreach (DataGridViewRow row in _elements.Rows)
            {
                row.Selected = ((FillElementRow)row.Tag!).Specifications.Count > 0 && !_elements.SelectedRows.Cast<DataGridViewRow>().Any();
            }

            FillCells(keepPosition: false);
        }

        public FillForm(UIApplication application, Document document, RevitQueue queue, PluginContext context, List<FillElementRow> rows)
            : base(application, document, queue, context)
        {
            UiKit.Setup(this, Tr.T("BIMinFormation — Preencher Parâmetros — ") + Document.Title, new Size(1280, 720));
            StartPosition = FormStartPosition.CenterScreen;

            _elements.Columns.Add(UiKit.TextColumn(Tr.T("ELEMENTO"), 50));
            _elements.Columns.Add(UiKit.TextColumn(Tr.T("FICHA"), 35));
            _elements.Columns.Add(UiKit.TextColumn(Tr.T("PENDÊNCIAS"), 15));

            _cells.ReadOnly = false;
            _cells.MultiSelect = false;
            _cells.SelectionMode = DataGridViewSelectionMode.CellSelect;
            _cells.EditMode = DataGridViewEditMode.EditOnEnter;
            _cells.Columns.Add(UiKit.TextColumn(Tr.T("PROPRIEDADE"), 34, false));
            _cells.Columns.Add(UiKit.TextColumn(Tr.T("FICHA"), 20, false));
            _cells.Columns.Add(UiKit.TextColumn(Tr.T("TIPO"), 14, false));
            _cells.Columns.Add(UiKit.TextColumn(Tr.T("VALOR"), 22, false));
            _cells.Columns.Add(UiKit.TextColumn(Tr.T("SITUAÇÃO"), 10, false));
            foreach (DataGridViewColumn column in _cells.Columns)
            {
                column.ReadOnly = column.Index != ValueColumn;
            }

            var filters = new FlowLayoutPanel { Dock = DockStyle.Top, Height = UiKit.Px(32), WrapContents = false };
            filters.Controls.Add(new Label { Text = Tr.T("Buscar:"), AutoSize = true, Padding = UiKit.Px(0, 6, 0, 0) });
            filters.Controls.Add(_search);
            filters.Controls.Add(_onlyPending);
            filters.Controls.Add(new Label { Text = Tr.T("Ficha:"), AutoSize = true, Padding = UiKit.Px(12, 6, 0, 0) });
            filters.Controls.Add(_spec);
            _copy = UiKit.Button(Tr.T("Copiar valores"), (s, e) => CopyValues(), 120);
            _paste = UiKit.Button(Tr.T("Colar valores"), (s, e) => PasteValues(), 120);
            filters.Controls.Add(_copy);
            filters.Controls.Add(_paste);
            _copy.Margin = UiKit.Px(16, 0, 0, 0);
            _copy.Enabled = _paste.Enabled = false;
            var top = new Panel { Dock = DockStyle.Top, Height = UiKit.Px(58), Padding = UiKit.Px(8, 4, 8, 0) };
            top.Controls.Add(filters);
            top.Controls.Add(_summary);

            // elements on the left with a third of the width; the property grid (more columns and text) takes the rest and is
            // the one that grows when the window grows
            var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, Width = ClientSize.Width };
            split.SplitterDistance = ClientSize.Width * 34 / 100;
            split.Panel1MinSize = UiKit.Px(260);
            split.Panel2MinSize = UiKit.Px(420);
            _split = split;
            split.Panel1.Controls.Add(_elements);
            split.Panel2.Controls.Add(_cells);

            _apply = UiKit.Button(Tr.T("Aplicar"), (s, e) => Apply(), 130);
            _link = UiKit.Button(Tr.T("Vincular ao Revit..."), (s, e) => LinkCurrent(), 150);
            _link.Enabled = false;
            var close = UiKit.Button(Tr.T("Fechar"), (s, e) => Close());
            Controls.Add(split);
            Controls.Add(top);
            Controls.Add(UiKit.Footer(_footer, _link, _apply, close));
            _cells.CurrentCellChanged += (s, e) => _link.Enabled = !Writing && CurrentLinkable() != null;

            _search.TextChanged += (s, e) => FillElements();
            _onlyPending.CheckedChanged += (s, e) => FillElements();
            _spec.SelectedIndexChanged += (s, e) =>
            {
                if (!_filling) FillElements();
            };
            _elements.ColumnHeaderMouseClick += (s, e) =>
            {
                _ascending = _sortColumn != e.ColumnIndex || !_ascending;
                _sortColumn = e.ColumnIndex;
                FillElements();
            };
            _elements.SelectionChanged += (s, e) =>
            {
                if (_filling) return;
                FillCells(keepPosition: false);
                UpdateSummary();
                SendSelection();
            };
            _elements.KeyDown += (s, e) =>
            {
                // Ctrl+C / Ctrl+V in the element list copy and paste the filled values (not the cell text)
                if (e.Control && e.KeyCode == Keys.C && _copy.Enabled) { CopyValues(); e.Handled = e.SuppressKeyPress = true; }
                else if (e.Control && e.KeyCode == Keys.V && _paste.Enabled) { PasteValues(); e.Handled = e.SuppressKeyPress = true; }
            };
            _elements.CellDoubleClick += (s, e) =>
            {
                if (e.RowIndex >= 0) ShowInRevit();
            };
            _cells.CellValueChanged += OnValueChanged;
            _cells.CellDoubleClick += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.ColumnIndex == ValueColumn) Unlock(e.RowIndex);
            };
            _cells.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (_cells.CurrentCell is DataGridViewComboBoxCell && _cells.IsCurrentCellDirty)
                {
                    _cells.CommitEdit(DataGridViewDataErrorContexts.Commit);
                }
            };
            _cells.DataError += (s, e) =>
            {
                e.ThrowException = false;
                Log.Info("Fill In Parameters: value outside the list in cell " + e.RowIndex + "/" + e.ColumnIndex);
            };
            _cells.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Delete && _cells.CurrentCell?.ColumnIndex == ValueColumn && !_cells.CurrentCell.ReadOnly)
                {
                    SetValue(_cells.CurrentCell.RowIndex, "");
                    e.Handled = true;
                }
            };
            FormClosed += (s, e) => Current = null;
            Current = this;
            SetRows(rows);
        }

        /// <summary>User property in the current row of the property grid (the one that can be linked).</summary>
        private FillCellModel? CurrentLinkable()
        {
            if (_cells.CurrentCell == null || !(_cells.Rows[_cells.CurrentCell.RowIndex].Tag is List<FillCellModel> group)) return null;
            var cell = group[0];
            return cell.Requirement.Kind == FacetKind.Property && cell.Conflict == null
                                                                  && (cell.Origin.Origin == BIMinFormation.Exporter.ValueOrigin.CustomParameter
                                                                      || cell.Origin.Origin == BIMinFormation.Exporter.ValueOrigin.ExporterQuantity && cell.Parameter != null
                                                                      && cell.QuantityValuesAccepted)
                ? cell
                : null;
        }

        /// <summary>
        /// A quantity the exporter calculates is locked; giving it a value replaces the calculation, so it asks first. Revit
        /// does not let the value go back to empty afterwards (only Ctrl+Z).
        /// </summary>
        private void Unlock(int rowIndex)
        {
            if (!(_cells.Rows[rowIndex].Tag is List<FillCellModel> group) || !group.All(c => c.LockedQuantity)) return;
            if (!group[0].QuantityValuesAccepted)
            {
                MessageBox.Show(this, group[0].Label + "\n\n" + Tr.T("Nesta versão do Revit o exportador não leva ao IFC uma quantidade informada ")
                                      + Tr.T("(só o Revit 2027): ela fica com o cálculo do exportador."), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string message = group[0].Label + "\n\n"
                             + Tr.T("O exportador calcula esta quantidade na exportação. Informar um valor substitui o cálculo em todo IFC ")
                             + Tr.T("exportado depois, e o Revit não deixa o parâmetro voltar a vazio (só o Ctrl+Z desfaz). Informar mesmo assim?");
            if (MessageBox.Show(this, message, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            foreach (var cell in group) cell.Unlocked = true;
            FillCells(keepPosition: true);
            if (rowIndex < _cells.Rows.Count)
            {
                _cells.CurrentCell = _cells.Rows[rowIndex].Cells[ValueColumn];
                _cells.BeginEdit(true);
            }
        }

        /// <summary>Link to Revit: picks the parameter and writes the link to the project profile.</summary>
        private void LinkCurrent()
        {
            var cell = CurrentLinkable();
            if (cell == null) return;
            Queue.Enqueue(Tr.T("vincular ao Revit"), app =>
            {
                var element = Document.GetElement(cell.ElementId);
                if (element == null) return;
                var type = Document.GetElement(element.GetTypeId()) as ElementType;
                string entity = Adapter.ModelReader.Entity(element, type).Entity ?? Core.Mapping.ExportMapping.FallbackEntity;
                entity = Context.SchemaCatalog.Entity(entity)?.Name ?? entity; // spelled as in the schema (IfcCovering)
                var requirement = cell.Requirement;
                var candidates = Adapter.RevitLinks.Candidates(element, type, requirement, Context.Exporter).Where(c => c.Compatible).ToList();
                bool text = IfcDataTypes.Classify(requirement.DataType) == IfcValueKind.String || requirement.DataType == null;
                string? material = text && LinkSuggestions.Words(requirement.Name ?? "").Contains("material")
                    ? Adapter.RevitLinks.MaterialNames(element, type) ?? Tr.T("(sem material)")
                    : null;
                string? weight = null;
                if (string.Equals(cell.DataType, "IFCMASSMEASURE", StringComparison.OrdinalIgnoreCase))
                {
                    double? kg = Adapter.RevitLinks.Weight(element, out string? problem);
                    weight = kg != null ? kg.Value.ToString("0.###") + " kg" : "(" + problem + ")";
                }
                var current = Context.Profile.Link(requirement.PropertySet!, requirement.Name!, entity);
                string detail = cell.TypeText + (requirement.DataType != null ? " (" + requirement.DataType + ")" : "") + " · " + cell.Specs
                                + Tr.T(" · elemento: ") + element.Name;
                using (var form = new LinkForm(requirement.PropertySet + " / " + requirement.Name, detail, entity, candidates, material, current, weight))
                {
                    if (form.ShowDialog(this) != DialogResult.OK) return;
                    var profile = Core.Profile.ProjectProfile.FromJson(Context.Profile.ToJson());
                    if (current != null) profile.SetLink(requirement.PropertySet!, requirement.Name!, current.Entity, null);
                    profile.SetLink(requirement.PropertySet!, requirement.Name!, form.OnlyEntity ? entity : null, form.Chosen,
                        form.ChosenCandidate?.BuiltIn, form.ChosenCandidate?.SharedGuid, form.ChosenCandidate?.Category);
                    using (var transaction = new Transaction(Document, Tr.T("BIMinFormation: vínculo")))
                    {
                        transaction.Start();
                        Adapter.ProfileStorage.Save(Document, profile);
                        transaction.Commit();
                    }

                    Log.Info("Link: " + requirement.PropertySet + "." + requirement.Name + " (" + (form.OnlyEntity ? entity : "all") + ") → "
                             + (form.Chosen ?? "no link"));
                }

                Rebuild(app);
            });
        }

        protected override void Rebuild(UIApplication application)
        {
            Context = PluginContext.Create(Document, out _) ?? Context;
            SetRows(FillService.BuildRows(Document, Context));
        }

        private void SetRows(List<FillElementRow> rows)
        {
            var selected = new HashSet<long>(SelectedRows().Select(r => r.Id.IdValue()));
            // edits not written yet survive the refresh, except where the cell is now linked to Revit or to the calculated material (the linked value
            // shows); project, building and site rows share the Project Information element, so the same key may repeat
            var edits = _rows.SelectMany(r => r.Cells).Where(c => c.Typed != null && !c.FromIds)
                .GroupBy(c => c.ElementId.IdValue() + "|" + c.Key).ToDictionary(g => g.Key, g => g.First().Typed);
            foreach (var cell in rows.SelectMany(r => r.Cells).Where(c => !c.Linked && !c.Derived))
            {
                if (edits.TryGetValue(cell.ElementId.IdValue() + "|" + cell.Key, out var typed))
                {
                    cell.Typed = typed;
                    cell.FromIds = false;
                }
            }

            _rows = rows;
            _filling = true;
            string? spec = _spec.SelectedItem as string;
            _spec.Items.Clear();
            _spec.Items.Add(AllSpecs);
            _spec.Items.AddRange(rows.SelectMany(r => r.Specifications).Select(s => s.Name).Distinct()
                .OrderBy(n => n, PortugueseComparer.Instance).Cast<object>().ToArray());
            _spec.SelectedItem = spec != null && _spec.Items.Contains(spec) ? spec : AllSpecs;
            _filling = false;
            FillElements(selected);
        }

        private IEnumerable<FillElementRow> VisibleRows()
        {
            string[] terms = PortugueseComparer.RemoveAccents(_search.Text).ToLowerInvariant()
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string? spec = _spec.SelectedItem as string;
            foreach (var row in _rows)
            {
                if (_onlyPending.Checked && row.Pending().Count == 0) continue;
                if (spec != null && spec != AllSpecs && row.Specifications.All(s => s.Name != spec)) continue;
                if (terms.Length > 0)
                {
                    string haystack = PortugueseComparer.RemoveAccents(row.ElementText + " " + string.Join(" ", row.Specifications.Select(s => s.Name))
                        + " " + string.Join(" ", row.Cells.Select(c => c.Label))).ToLowerInvariant();
                    if (terms.Any(t => !haystack.Contains(t))) continue;
                }

                yield return row;
            }
        }

        private void FillElements(ISet<long>? selected = null)
        {
            selected ??= new HashSet<long>(SelectedRows().Select(r => r.Id.IdValue()));
            var visible = VisibleRows();
            if (_sortColumn >= 0)
            {
                Func<FillElementRow, string> key = _sortColumn switch
                {
                    0 => r => r.ElementText,
                    1 => r => r.SpecText,
                    _ => r => r.Pending().Count.ToString("D5"),
                };
                visible = _ascending
                    ? visible.OrderBy(key, PortugueseComparer.Instance).ThenBy(r => r.Order)
                    : visible.OrderByDescending(key, PortugueseComparer.Instance).ThenBy(r => r.Order);
            }
            else
            {
                // with a specification first, in model order
                visible = visible.OrderBy(r => r.Specifications.Count > 0 ? 0 : 1).ThenBy(r => r.Order);
            }

            _filling = true;
            try
            {
                _elements.Rows.Clear();
                foreach (var row in visible)
                {
                    var pending = row.Pending();
                    int index = _elements.Rows.Add(row.ElementText, row.SpecText, pending.Count == 0 ? "" : pending.Count.ToString());
                    var gridRow = _elements.Rows[index];
                    gridRow.Tag = row;
                    gridRow.Cells[1].ToolTipText = string.Join("\n", row.Specifications.Select(s => s.Name).Concat(row.Notes.Distinct()));
                    gridRow.Cells[2].ToolTipText = string.Join("\n", pending.Select(c => c.Label));
                    gridRow.Cells[2].Style.ForeColor = UiKit.Pending;
                    if (row.Specifications.Count == 0) gridRow.DefaultCellStyle.ForeColor = UiKit.Muted;
                    gridRow.Selected = selected.Contains(row.Id.IdValue());
                }

                UiKit.ShowSort(_elements, _sortColumn, _ascending);
            }
            finally
            {
                _filling = false;
            }

            FillCells(keepPosition: false);
            UpdateSummary();
        }

        private List<FillElementRow> SelectedRows() =>
            _elements.SelectedRows.Cast<DataGridViewRow>().Select(r => (FillElementRow)r.Tag!).ToList();

        /// <summary>Requirements shared by the selected elements (intersection by key).</summary>
        private List<List<FillCellModel>> SharedCells()
        {
            var rows = SelectedRows().Where(r => r.Cells.Count > 0).ToList();
            if (rows.Count == 0) return new List<List<FillCellModel>>();
            var keys = rows.Skip(1).Aggregate(new HashSet<string>(rows[0].Cells.Select(c => c.Key)),
                (set, row) => { set.IntersectWith(row.Cells.Select(c => c.Key)); return set; });
            bool hide = Context.Profile.HideWhenNoValue;
            return rows[0].Cells.Where(c => keys.Contains(c.Key) && (!hide || c.IsWritable || c.Linked || c.LockedQuantity))
                .Select(c => rows.Select(r => r.Cells.First(x => x.Key == c.Key)).ToList())
                .ToList();
        }

        private void FillCells(bool keepPosition)
        {
            int row = keepPosition ? _cells.CurrentCell?.RowIndex ?? -1 : -1;
            int scroll = keepPosition ? _cells.FirstDisplayedScrollingRowIndex : -1;
            _filling = true;
            try
            {
                _cells.Rows.Clear();
                foreach (var group in SharedCells())
                {
                    var first = group[0];
                    string value = Common(group);
                    int index = _cells.Rows.Add(first.Label, first.Specs, first.TypeText, value, Status(group));
                    var gridRow = _cells.Rows[index];
                    gridRow.Tag = group;
                    DataGridViewCell valueCell = gridRow.Cells[ValueColumn];
                    // material link: the materials every selected element has
                    var materials = first.MaterialOptions?.Where(m => group.All(c => c.MaterialOptions?.Contains(m) == true)).ToList();
                    if (materials != null && materials.Count == 0) materials = null;
                    if (first.IsWritable && (first.Choices != null || first.Requirement.AllowedValues != null || first.IsBoolean || materials != null))
                    {
                        var options = first.IsBoolean ? new List<string> { "true", "false" }
                            : (first.Choices ?? first.Requirement.AllowedValues ?? materials!).ToList();
                        var combo = new DataGridViewComboBoxCell { FlatStyle = FlatStyle.Flat };
                        combo.Items.Add(Empty);
                        combo.Items.AddRange(options.Cast<object>().ToArray());
                        if (value.Length > 0 && !combo.Items.Contains(value)) combo.Items.Add(value);
                        gridRow.Cells[ValueColumn] = combo;
                        combo.Value = value.Length == 0 ? Empty : value;
                        valueCell = combo;
                    }

                    valueCell.ReadOnly = !first.IsWritable || first.CopyFrom != null || first.WeightLink; // linked: the source value
                    var result = first.Evaluate();
                    gridRow.Cells[0].ToolTipText = Hint(first);
                    gridRow.Cells[4].ToolTipText = result.Message;
                    var tips = new List<string>();
                    if (first.ModelValue != null && !first.ModelValue.IsEmpty && first.Typed != null)
                    {
                        tips.Add(Tr.T("No modelo: ") + first.ModelValue);
                    }

                    // the SI value as the project shows it (the user may work in mm, cm, degrees…)
                    if (value.Length > 0 && Adapter.RevitSpecs.InProject(Document, first.Spec, value) is { } inProject)
                    {
                        tips.Add(Tr.T("Na unidade do projeto: ") + inProject);
                    }

                    if (tips.Count > 0) valueCell.ToolTipText = string.Join("\n", tips);

                    if (!first.IsWritable)
                    {
                        gridRow.DefaultCellStyle.ForeColor = UiKit.Muted;
                    }

                    PaintStatus(gridRow.Cells[4], result.Status);
                }

                if (row >= 0 && row < _cells.Rows.Count)
                {
                    _cells.CurrentCell = _cells.Rows[row].Cells[ValueColumn];
                    if (scroll >= 0 && scroll < _cells.Rows.Count) _cells.FirstDisplayedScrollingRowIndex = scroll;
                }
            }
            finally
            {
                _filling = false;
            }
        }

        private static string Hint(FillCellModel cell)
        {
            var lines = new List<string> { cell.TypeText + (cell.DataType != null ? " (" + cell.DataType + ")" : "") };
            if (cell.Requirement.AllowedValues != null) lines.Add(Tr.T("Valores: ") + string.Join(", ", cell.Requirement.AllowedValues));
            if (cell.IsWritable && Core.Fill.UnitInput.Accepted(cell.DataType) is { } units)
            {
                lines.Add(Tr.T("Digite no SI (como o IDS) ou com a unidade: ") + units);
            }
            if (cell.Linked) lines.Add(Tr.T("Vinculado ao parâmetro do Revit \"") + cell.Parameter + Tr.T("\": o exportador lê o valor na hora de exportar."));
            else if (cell.CopyFrom != null) lines.Add(Tr.T("Vinculado ao parâmetro do Revit \"") + cell.CopyFrom
                                                      + Tr.T("\": o plugin copia o valor para o próprio parâmetro a cada Aplicar e Exportar (no Revit 2027, ")
                                                      + Tr.T("o exportador leria o parâmetro vinculado em todos os elementos do conjunto)."));
            else if (cell.WeightLink) lines.Add(Tr.T("Peso calculado pelo plugin (volume de cada material × densidade dos recursos físicos dele) e regravado a cada Aplicar e Exportar."));
            else if (cell.LockedQuantity) lines.Add(cell.QuantityValuesAccepted
                ? Tr.T("Calculada pelo exportador na exportação. Duplo clique no valor para informar um (substitui o cálculo).")
                : Tr.T("Calculada pelo exportador na exportação (valor informado só sai exportando do Revit 2027)."));
            else if (cell.Derived) lines.Add(Tr.T("Calculado pelo plugin (material do elemento) e regravado a cada Aplicar e Exportar."));
            if (cell.Parameter != null && !cell.Linked) lines.Add(Tr.T("Parâmetro: ") + cell.Parameter + (cell.OnType ? Tr.T(" (no tipo)") : ""));
            if (cell.Origin.Detail != null) lines.Add(cell.Origin.Detail);
            string? instructions = cell.Requirement.Facets.Select(f => f.Instructions).FirstOrDefault(i => !string.IsNullOrWhiteSpace(i));
            if (instructions != null) lines.Add(Tr.T("Instrução do .ids: ") + instructions);
            return string.Join("\n", lines);
        }

        private static void PaintStatus(DataGridViewCell cell, CellStatus status)
        {
            cell.Style.ForeColor = status switch
            {
                CellStatus.Ok => UiKit.Done,
                CellStatus.Pending => UiKit.Pending,
                CellStatus.Invalid => UiKit.Pending,
                CellStatus.Prohibited => UiKit.Pending,
                CellStatus.Changed => UiKit.Warning,
                _ => UiKit.Muted,
            };
        }

        private static string Common(List<FillCellModel> group)
        {
            var values = group.Select(c => c.ValueText).Distinct().ToList();
            return values.Count == 1 ? values[0] : MixedText;
        }

        private string Status(List<FillCellModel> group)
        {
            var values = group.Select(c => c.StatusText()).Distinct().ToList();
            return values.Count == 1 ? values[0] : MixedText;
        }

        private void OnValueChanged(object? sender, DataGridViewCellEventArgs e)
        {
            if (_filling || e.RowIndex < 0 || e.ColumnIndex != ValueColumn)
            {
                return;
            }

            string text = Convert.ToString(_cells.Rows[e.RowIndex].Cells[ValueColumn].Value) ?? "";
            if (text == MixedText)
            {
                return;
            }

            SetValue(e.RowIndex, text == Empty ? "" : text);
        }

        /// <summary>Applies the same value to every selected element and recomputes the status.</summary>
        private void SetValue(int rowIndex, string text)
        {
            var group = (List<FillCellModel>)_cells.Rows[rowIndex].Tag!;
            string? note = null;
            foreach (var cell in group)
            {
                string value = text;
                if (IfcDataTypes.Classify(cell.DataType) == IfcValueKind.Real)
                {
                    // a value with its unit ("200 mm", "30°", "20 °C") goes to the SI unit the IDS uses
                    string? si = Core.Fill.UnitInput.ToSi(text, cell.DataType, out string? problem);
                    value = si ?? text.Trim().Replace(',', '.');
                    note = si != null ? text.Trim() + " = " + si + Tr.T(" (SI, como o IDS pede)") : problem;
                }

                cell.Typed = cell.ModelValue != null && !cell.ModelValue.IsEmpty && cell.ModelValue.ToString() == value ? null : value;
                cell.FromIds = false;
            }

            BeginInvoke(new Action(() =>
            {
                FillCells(keepPosition: true);
                RefreshElementCounts();
                UpdateSummary();
                if (note != null) _footer.Text = note;
            }));
        }

        private void RefreshElementCounts()
        {
            foreach (DataGridViewRow gridRow in _elements.Rows)
            {
                var pending = ((FillElementRow)gridRow.Tag!).Pending();
                gridRow.Cells[2].Value = pending.Count == 0 ? "" : pending.Count.ToString();
                gridRow.Cells[2].ToolTipText = string.Join("\n", pending.Select(c => c.Label));
            }
        }

        private int ToWrite() => _rows.Sum(r => r.Cells.Count(c => c.ToWrite()));

        private void UpdateSummary()
        {
            int total = _rows.Count;
            int withSpec = _rows.Count(r => r.Specifications.Count > 0);
            int pending = _rows.Sum(r => r.Pending().Count);
            int toWrite = ToWrite();
            _summary.Text = Tr.T("Elementos: ") + _elements.Rows.Count + Tr.T(" de ") + total + Tr.T(" · Com ficha: ") + withSpec + Tr.T(" · Sem ficha: ")
                            + (total - withSpec) + Tr.T(" · Pendências: ") + pending + Tr.T(" · A gravar: ") + toWrite + Tr.T(" · Selecionados: ")
                            + _elements.SelectedRows.Count;
            var selected = SelectedRows();
            _copy.Enabled = selected.Count == 1 && selected[0].Cells.Count > 0;
            _paste.Enabled = _clipboard != null && selected.Count > 0 && !Writing;
            _paste.Text = _clipboard == null ? Tr.T("Colar valores") : Tr.T("Colar valores (") + _clipboard.Count + ")";
            _apply.Text = Tr.T("Aplicar (") + toWrite + ")";
            _apply.Enabled = toWrite > 0 && !Writing;
            _footer.Text = Context.Status.Summary;
        }

        /// <summary>Copies the filled values of the selected element (a single one).</summary>
        internal void CopyValues()
        {
            var selected = SelectedRows();
            if (selected.Count != 1) return;
            _clipboard = FillService.CopyValues(selected[0]);
            _clipboardSource = selected[0].Name;
            UpdateSummary();
            _footer.Text = _clipboard.Count + Tr.T(" valor(es) copiado(s) de ") + _clipboardSource + Tr.T(". Selecione os elementos e cole (Ctrl+V).");
        }

        /// <summary>Pastes the copied values into the selected elements; written only on Apply.</summary>
        internal (int Pasted, int Rejected) PasteValues()
        {
            if (_clipboard == null) return (0, 0);
            var targets = SelectedRows().Where(r => r.Cells.Count > 0).ToList();
            var (pasted, rejected) = FillService.PasteValues(targets, _clipboard);
            FillCells(keepPosition: true);
            RefreshElementCounts();
            UpdateSummary();
            _footer.Text = pasted + Tr.T(" valor(es) colado(s) em ") + targets.Count + Tr.T(" elemento(s) de ") + _clipboardSource
                           + (rejected > 0 ? "; " + rejected + Tr.T(" recusado(s) (o destino não aceita o valor)") : "")
                           + Tr.T(". Confira e clique em Aplicar.");
            return (pasted, rejected);
        }

        /// <summary>Selects elements in the list (tests).</summary>
        internal void SelectElements(ISet<long> ids) => SelectInGrid(ids);

        private void Apply()
        {
            int cells = ToWrite();
            int elements = _rows.Count(r => r.Cells.Any(c => c.ToWrite()));
            if (cells == 0
                || MessageBox.Show(this, Tr.T("Serão gravadas ") + cells + Tr.T(" propriedade(s) em ") + elements
                                         + Tr.T(" elemento(s). O modelo não é salvo automaticamente. Continuar?"), Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            Writing = true;
            _apply.Enabled = false;
            Queue.Enqueue(Tr.T("aplicar o Preencher"), app =>
            {
                try
                {
                    var summary = FillService.Apply(Document, Context, _rows, ParametersFile);
                    Log.Info("Fill In Parameters: " + summary.ToString().Replace("\n", " "));
                    foreach (var cell in _rows.SelectMany(r => r.Cells))
                    {
                        cell.Typed = null; // the grid is read again from the model
                    }

                    Rebuild(app);
                    TaskDialog.Show(Tr.T("BIMinFormation — Preencher Parâmetros"), summary.ToString());
                }
                finally
                {
                    Writing = false;
                    UpdateSummary();
                }
            });
        }

        protected override IReadOnlyCollection<ElementId> SelectedIds() => SelectedRows().Select(r => r.Id).ToList();

        protected override void SelectInGrid(ISet<long> ids)
        {
            _filling = true;
            try
            {
                foreach (DataGridViewRow row in _elements.Rows)
                {
                    row.Selected = ids.Contains(((FillElementRow)row.Tag!).Id.IdValue());
                }
            }
            finally
            {
                _filling = false;
            }

            FillCells(keepPosition: false);
            UpdateSummary();
        }
    }
}
