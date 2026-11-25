using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMinFormation.Core.Prevalidation;
using BIMinFormation.Revit.Services;
using Color = System.Drawing.Color;
using Panel = System.Windows.Forms.Panel;
using BIMinFormation.Ids;

namespace BIMinFormation.Revit.UI
{
    /// <summary>
    /// Pre-validation: the specifications checked on the model, before exporting. On the left, each specification with the
    /// elements that pass, fail and are to check (what only the IFC confirms: quantities, material, what the exporter
    /// calculates); on the right, the elements of the specification with the reason. The selection follows Revit both ways.
    /// </summary>
    internal sealed class PrevalidationForm : SyncedForm
    {
        private const string OrphanRow = "\u0001orfaos";

        public static PrevalidationForm? Current { get; private set; }

        private readonly DataGridView _specs = UiKit.Grid();
        private readonly DataGridView _elements = UiKit.Grid();
        private readonly Label _summary = new Label { Dock = DockStyle.Top, Height = UiKit.Px(22), ForeColor = UiKit.Muted };
        // element detail: every requirement, with scrolling (the list of a specification goes over 30 lines)
        private readonly RichTextBox _detail = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            DetectUrls = false,
            BackColor = SystemColors.Window,
        };

        /// <summary>Detail text (tests).</summary>
        internal string DetailText => _detail.Text;
        private readonly CheckBox _onlyWithElements = new CheckBox { Text = Tr.T("Só fichas com elementos"), AutoSize = true, Checked = true };
        private readonly CheckBox _colorView = new CheckBox { Text = Tr.T("Colorir a vista ativa"), AutoSize = true, Padding = UiKit.Px(16, 0, 0, 0) };
        private static Label Legend(string text, Adapter.ViewColor color)
        {
            var c = Adapter.ViewColors.ColorOf(color);
            return new Label { Text = "■ " + text, AutoSize = true, Padding = UiKit.Px(8, 3, 0, 0), ForeColor = Color.FromArgb(c.Red, c.Green, c.Blue) };
        }
        private readonly Adapter.ViewColors _colors = new Adapter.ViewColors();

        /// <summary>Colors applied by the window (tests).</summary>
        internal Adapter.ViewColors ViewColors => _colors;
        private readonly Label _footer = new Label();
        private readonly Button _back;
        private PrevalidationResult _result;
        private Dictionary<string, ElementInfo> _infos = new Dictionary<string, ElementInfo>();
        private readonly string _project;
        private bool _filling;

        internal int SpecRowCount => _specs.Rows.Count;
        internal int ElementRowCount => _elements.Rows.Count;
        internal string SummaryText => _summary.Text;

        public PrevalidationForm(UIApplication application, Document document, RevitQueue queue, PluginContext context, PrevalidationResult result)
            : base(application, document, queue, context)
        {
            _result = result;
            _project = document.Title;
            UiKit.Setup(this, Tr.T("BIMinFormation — Pré-validação — ") + Document.Title, new Size(1200, 680));
            StartPosition = FormStartPosition.CenterScreen;

            _specs.Columns.Add(UiKit.TextColumn(Tr.T("FICHA"), 52, false));
            _specs.Columns.Add(UiKit.TextColumn(Tr.T("APLICÁVEIS"), 12, false));
            _specs.Columns.Add(UiKit.TextColumn(Tr.T("OK"), 12, false));
            _specs.Columns.Add(UiKit.TextColumn(Tr.T("FALHAM"), 12, false));
            _specs.Columns.Add(UiKit.TextColumn(Tr.T("A CONFERIR"), 12, false));
            _specs.MultiSelect = false;
            _specs.Columns[2].ToolTipText = Tr.T("Passam, ou estão ok no modelo e só faltam as quantidades que o exportador calcula.");
            _specs.Columns[4].ToolTipText = Tr.T("Só o IFC confirma: material, Pset padrão vazio, classificação, atributos que o exportador preenche.");
            _elements.Columns.Add(UiKit.TextColumn(Tr.T("ELEMENTO"), 34, false));
            _elements.Columns.Add(UiKit.TextColumn(Tr.T("SITUAÇÃO"), 12, false));
            _elements.Columns.Add(UiKit.TextColumn(Tr.T("MOTIVO"), 54, false));

            var options = new FlowLayoutPanel { Dock = DockStyle.Top, Height = UiKit.Px(28), Padding = UiKit.Px(0, 2, 0, 0) };
            options.Controls.Add(_onlyWithElements);
            var pane = UiKit.Button(Tr.T("Painel"), (s, e) => Queue.Enqueue(Tr.T("painel de conformidade"), app =>
            {
                bool shown = PaneController.Toggle(app);
                _footer.Text = shown ? Tr.T("Painel de conformidade aberto ao lado da vista.") : Tr.T("Painel de conformidade fechado.");
            }), 80);
            pane.Margin = UiKit.Px(16, 0, 0, 0);
            new ToolTip().SetToolTip(pane, Tr.T("Mostra ou esconde o painel de conformidade ao lado da vista (atualiza enquanto você modela)."));
            options.Controls.Add(pane);
            options.Controls.Add(_colorView);
            options.Controls.Add(Legend(Tr.T("falha"), Adapter.ViewColor.Fail));
            options.Controls.Add(Legend(Tr.T("a conferir"), Adapter.ViewColor.ToCheck));
            options.Controls.Add(Legend("ok", Adapter.ViewColor.Ok));
            var top = new Panel { Dock = DockStyle.Top, Height = UiKit.Px(54), Padding = UiKit.Px(8, 4, 8, 0) };
            top.Controls.Add(options);
            top.Controls.Add(_summary);

            // elements on top, detail below, with a draggable splitter
            var right = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Height = ClientSize.Height };
            right.SplitterDistance = ClientSize.Height * 50 / 100;
            right.Panel1MinSize = UiKit.Px(80);
            right.Panel2MinSize = UiKit.Px(80);
            right.Panel1.Controls.Add(_elements);
            right.Panel2.Padding = UiKit.Px(6, 4, 2, 2);
            right.Panel2.BackColor = SystemColors.Window;
            right.Panel2.Controls.Add(_detail);
            var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, Width = ClientSize.Width };
            split.SplitterDistance = ClientSize.Width * 42 / 100;
            split.Panel1MinSize = UiKit.Px(300);
            split.Panel2MinSize = UiKit.Px(380);
            split.Panel1.Controls.Add(_specs);
            split.Panel2.Controls.Add(right);

            // the window follows the model changes by itself; after an IFC check, this goes back to the model-only result
            _back = UiKit.Button(Tr.T("Voltar ao modelo"), (s, e) => RequestRebuild(), 130);
            _back.Visible = false;
            var probe = UiKit.Button(Tr.T("Conferir no IFC"), (s, e) => RequestProbe(), 130);
            new ToolTip().SetToolTip(probe, Tr.T("Exporta um IFC temporário, como o botão Exportar, e avalia as fichas nele: o que está a conferir ")
                                            + Tr.T("vira passa ou falha. O modelo não muda. Em modelos grandes leva o tempo de uma exportação."));
            // outputs in a menu: pending items, BCF, report and specification table for the BEP
            var menu = new ContextMenuStrip();
            menu.Items.Add(Tr.T("Pendências (CSV)..."), null, (s, e) => Save("csv"));
            menu.Items.Add(Tr.T("Falhas em BCF..."), null, (s, e) => Save("bcf"));
            menu.Items.Add(Tr.T("Relatório para o contratante..."), null, (s, e) => Save("html"));
            menu.Items.Add(Tr.T("Quadro de fichas para o BEP (CSV)..."), null, (s, e) => Save("bep"));
            Button? save = null;
            save = UiKit.Button(Tr.T("Salvar ▾"), (s, e) => menu.Show(save!, new System.Drawing.Point(0, save!.Height)), 100);
            var close = UiKit.Button(Tr.T("Fechar"), (s, e) => Close());
            Controls.Add(split);
            Controls.Add(top);
            Controls.Add(UiKit.Footer(_footer, probe, save, _back, close));
            CancelButton = close;

            _onlyWithElements.CheckedChanged += (s, e) => FillSpecs();
            _colorView.CheckedChanged += (s, e) =>
            {
                if (_filling) return;
                bool on = _colorView.Checked;
                Queue.Enqueue(on ? Tr.T("colorir a vista") : Tr.T("limpar as cores"), app => UpdateColors(app, on));
            };
            // the colors are saved in the view: when the window closes, the original appearance comes back
            FormClosed += (s, e) =>
            {
                if (_colors.IsActive) Queue.Enqueue(Tr.T("limpar as cores"), app => UpdateColors(app, false));
            };
            _specs.SelectionChanged += (s, e) =>
            {
                if (!_filling) FillElements();
            };
            _elements.SelectionChanged += (s, e) =>
            {
                if (_filling) return;
                ShowDetail();
                SendSelection();
            };
            _elements.CellDoubleClick += (s, e) =>
            {
                if (e.RowIndex >= 0) ShowInRevit();
            };
            FormClosed += (s, e) => Current = null;
            Current = this;
            _infos = PrevalidationService.Infos(document);
            FillSpecs();
        }

        /// <summary>Pending items (CSV), BCF or report for the client, from the result shown in the window.</summary>
        private void Save(string kind)
        {
            string name = string.Join("_", _project.Split(System.IO.Path.GetInvalidFileNameChars()));
            using var picker = new SaveFileDialog
            {
                Title = kind == "csv" ? Tr.T("Pendências da pré-validação") : kind == "bcf" ? Tr.T("Tópicos BCF das falhas")
                    : kind == "bep" ? Tr.T("Quadro de fichas do projeto para o BEP") : Tr.T("Relatório para o contratante"),
                Filter = kind == "csv" || kind == "bep" ? Tr.T("CSV (*.csv)|*.csv") : kind == "bcf" ? Tr.T("BCF 2.1 (*.bcf)|*.bcf") : Tr.T("Página (*.html)|*.html"),
                FileName = name + (kind == "csv" ? "-pendencias.csv" : kind == "bcf" ? "-falhas.bcf" : kind == "bep" ? "-fichas-bep.csv" : "-relatorio.html"),
            };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                if (kind == "bep")
                {
                    PrevalidationService.WriteSpecificationTable(_result, Context, picker.FileName);
                    _footer.Text = Tr.T("Gravado: ") + picker.FileName;
                    return;
                }

                int topics = PrevalidationService.WriteReports(_result, Context, _infos, _project,
                    kind == "csv" ? picker.FileName : null, kind == "bcf" ? picker.FileName : null, kind == "html" ? picker.FileName : null);
                _footer.Text = kind == "bcf" ? topics + Tr.T(" tópico(s) em ") + picker.FileName : Tr.T("Gravado: ") + picker.FileName;
                if (kind == "html") System.Diagnostics.Process.Start(picker.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, Tr.T("Não deu para gravar: ") + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>Selects elements in the list of the current specification (tests).</summary>
        internal void SelectElements(ISet<long> ids) => SelectInGrid(ids);

        /// <summary>Selects the specification in the list (request from the conformity panel).</summary>
        internal void SelectSpecification(string name)
        {
            var row = _specs.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => (string)r.Tag! == name);
            if (row == null) return;
            _specs.ClearSelection();
            row.Selected = true;
            _specs.FirstDisplayedScrollingRowIndex = row.Index;
        }

        /// <summary>Check in the IFC: exports, reads the IFC and shows its result on top of the model.</summary>
        internal void RequestProbe()
        {
            _footer.Text = Tr.T("Exportando o IFC de conferência… (o Revit fica ocupado durante a exportação)");
            _footer.Refresh();
            Queue.Enqueue(Tr.T("conferência no IFC"), application =>
            {
                string? problem = Probe(application);
                if (problem != null && !IsDisposed)
                {
                    MessageBox.Show(this, Tr.T("Não deu para conferir no IFC:") + "\n\n" + problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            });
        }

        /// <summary>Runs the check and shows the result; returns the reason when the check could not be done.</summary>
        internal string? Probe(UIApplication application)
        {
            Writing = true; // the preparation writes and undoes: not a user change
            PrevalidationResult? probed;
            string? problem;
            try
            {
                probed = ProbeService.Run(Document, Context, out problem);
            }
            catch (Exception ex)
            {
                Log.Error("Check in the IFC", ex);
                probed = null;
                problem = ex.Message;
            }
            finally
            {
                Writing = false;
            }

            if (IsDisposed) return problem;
            if (probed == null)
            {
                _footer.Text = Tr.T("Conferência no IFC não feita.");
                return problem ?? "";
            }

            Context = PluginContext.Create(Document, out _) ?? Context;
            _result = probed;
            _infos = PrevalidationService.Infos(Document);
            FillSpecs();
            if (_colorView.Checked) UpdateColors(application, true);
            return null;
        }

        protected override void Rebuild(UIApplication application)
        {
            Context = PluginContext.Create(Document, out _) ?? Context;
            _result = PrevalidationService.Run(Document, Context);
            _infos = PrevalidationService.Infos(Document);
            FillSpecs();
            if (_colorView.Checked) UpdateColors(application, true);
        }

        /// <summary>Colors the active view (or clears the colors) in its own transaction, without triggering another refresh.</summary>
        internal void UpdateColors(UIApplication application, bool on)
        {
            if (!Document.IsValidObject) return;
            var view = application.ActiveUIDocument?.Document.Equals(Document) == true ? application.ActiveUIDocument.ActiveView : null;
            if (on && (view == null || !view.AreGraphicsOverridesAllowed()))
            {
                _footer.Text = Tr.T("A vista ativa não aceita cores (tabela, folha…): abra uma vista de planta, corte ou 3D.");
                return;
            }

            Writing = true;
            try
            {
                using var transaction = new Transaction(Document, on ? Tr.T("BIMinFormation: cores da pré-validação") : Tr.T("BIMinFormation: limpar cores"));
                transaction.Start();
                int count = on ? _colors.Apply(view!, PrevalidationService.Colors(_result)) : _colors.Clear(Document);
                transaction.Commit();
                if (on && !IsDisposed) _footer.Text = count + Tr.T(" elemento(s) coloridos em \"") + view!.Name + Tr.T("\". As cores saem ao desmarcar ou fechar a janela.");
            }
            finally
            {
                Writing = false;
            }
        }

        private void FillSpecs()
        {
            string? selected = _specs.SelectedRows.Count > 0 ? _specs.SelectedRows[0].Tag as string : null;
            _filling = true;
            try
            {
                _specs.Rows.Clear();
                if (_result.Orphans.Count > 0)
                {
                    int index = _specs.Rows.Add(Tr.T("Identidade sem ficha (EOI que não casa com nenhuma ficha)"), _result.Orphans.Count, "",
                        _result.Orphans.Count, "");
                    _specs.Rows[index].Tag = OrphanRow;
                    _specs.Rows[index].DefaultCellStyle.ForeColor = UiKit.Pending;
                }

                foreach (var check in _result.Specifications.OrderBy(s => s.Specification.Name, Core.Classification.PortugueseComparer.Instance))
                {
                    if (_onlyWithElements.Checked && check.Elements.Count == 0) continue;
                    int index = _specs.Rows.Add(check.Specification.Name + (check.MissingRequired ? Tr.T(" (obrigatória no .ids; nenhum elemento)") : ""),
                        check.Elements.Count, check.Ok, check.Failed, check.ToCheck);
                    var row = _specs.Rows[index];
                    row.Tag = check.Specification.Name;
                    if (check.Elements.Count == 0) row.DefaultCellStyle.ForeColor = UiKit.Muted;
                    row.Cells[3].Style.ForeColor = check.Failed > 0 ? UiKit.Pending : UiKit.Muted;
                    row.Cells[2].Style.ForeColor = check.Ok > 0 ? UiKit.Done : UiKit.Muted;
                    row.Cells[4].Style.ForeColor = check.ToCheck > 0 ? UiKit.Warning : UiKit.Muted;
                }

                _specs.ClearSelection();
                var again = _specs.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => (string)r.Tag! == selected)
                            ?? (_specs.Rows.Count > 0 ? _specs.Rows[0] : null);
                if (again != null) again.Selected = true;
            }
            finally
            {
                _filling = false;
            }

            var all = _result.Specifications.SelectMany(s => s.Elements).ToList();
            int missing = _result.Specifications.Count(s => s.MissingRequired);
            _summary.Text = Tr.T("Fichas com elementos: ") + _result.Specifications.Count(s => s.Elements.Count > 0) + Tr.T(" de ") + _result.Specifications.Count
                            + Tr.T(" · verificações: ") + all.Count + " (ok " + all.Count(e => e.Status == CheckStatus.Pass || e.OkInModel)
                            + Tr.T(", falham ") + all.Count(e => e.Status == CheckStatus.Fail) + Tr.T(", a conferir ")
                            + all.Count(e => e.Status == CheckStatus.Unknown && !e.OkInModel) + ")"
                            + (_result.Orphans.Count > 0 ? Tr.T(" · identidade sem ficha: ") + _result.Orphans.Count : "")
                            + (missing > 0 ? " · " + missing + Tr.T(" ficha(s) obrigatória(s) no .ids sem elemento no modelo") : "");
            var probe = _result.Probe;
            _back.Visible = probe != null;
            if (probe != null)
            {
                _summary.Text += Tr.T(" · conferido no IFC (") + Core.Profile.ProfileStatus.Name(probe.Schema)
                                 + ", " + probe.When.ToString("HH:mm") + ")";
                var notes = new List<string> { probe.Decided + Tr.T(" verificação(ões) decidida(s) pelo IFC (") + probe.Resolved + Tr.T(" estavam a conferir)") };
                if (probe.NotExported > 0) notes.Add(probe.NotExported + Tr.T(" não saíram no IFC"));
                if (probe.NotApplicableInIfc > 0) notes.Add(probe.NotApplicableInIfc + Tr.T(" fora da ficha no IFC"));
                _footer.Text = string.Join("; ", notes) + Tr.T(". \"Voltar ao modelo\" mostra o resultado só do modelo.");
            }
            else
            {
                _footer.Text = Tr.T("Ok no modelo: só faltam quantidades do exportador. Duplo clique aproxima.");
            }
            FillElements();
        }

        private void FillElements()
        {
            _filling = true;
            try
            {
                _elements.Rows.Clear();
                string? spec = _specs.SelectedRows.Count > 0 ? _specs.SelectedRows[0].Tag as string : null;
                if (spec == OrphanRow)
                {
                    foreach (var orphan in _result.Orphans)
                    {
                        int index = _elements.Rows.Add(ElementName(orphan.Element.Id), Tr.T("sem ficha"), orphan.OrphanValue + Tr.T(" não é o nome de nenhuma ficha"));
                        _elements.Rows[index].Tag = orphan.Element.Id;
                        _elements.Rows[index].Cells[1].Style.ForeColor = UiKit.Pending;
                    }
                }
                else if (spec != null)
                {
                    var check = _result.Specifications.First(s => s.Specification.Name == spec);
                    if (check.Elements.Count == 0)
                    {
                        int index = _elements.Rows.Add(Tr.T("(nenhum elemento do modelo)"), "",
                            check.MissingRequired ? Tr.T("o .ids marca a ficha como obrigatória; vale se o BEP pede esta ficha no projeto") : "");
                        _elements.Rows[index].Tag = "";
                        _elements.Rows[index].DefaultCellStyle.ForeColor = UiKit.Muted;
                    }

                    foreach (var element in check.Elements.OrderBy(e => e.Status == CheckStatus.Fail ? 0 : e.Status == CheckStatus.Unknown && !e.OkInModel ? 1 : 2))
                    {
                        int index = _elements.Rows.Add(ElementName(element.Element.Id), PrevalidationService.StatusText(element),
                            PrevalidationService.Reason(element));
                        var row = _elements.Rows[index];
                        row.Tag = element.Element.Id;
                        row.Cells[1].Style.ForeColor = element.Status == CheckStatus.Pass || element.OkInModel ? UiKit.Done
                            : element.Status == CheckStatus.Fail ? UiKit.Pending : UiKit.Warning;
                    }
                }

                _elements.ClearSelection();
            }
            finally
            {
                _filling = false;
            }

            ShowDetail();
        }

        private string ElementName(string id) =>
            _infos.TryGetValue(id, out var info) ? info.Name + " · " + info.Category + " · ID " + id : "ID " + id;

        private void ShowDetail()
        {
            string? spec = _specs.SelectedRows.Count > 0 ? _specs.SelectedRows[0].Tag as string : null;
            string? id = _elements.SelectedRows.Count > 0 ? _elements.SelectedRows[0].Tag as string : null;
            var check = spec == null || spec == OrphanRow ? null : _result.Specifications.FirstOrDefault(s => s.Specification.Name == spec);
            var element = check?.Elements.FirstOrDefault(e => e.Element.Id == id);
            _detail.Clear();
            if (element == null)
            {
                _detail.SelectionColor = UiKit.Muted;
                _detail.AppendText(check == null ? "" : check.Specification.Name + (check.Specification.Description != null ? "\n" + check.Specification.Description : ""));
                return;
            }

            _detail.SelectionColor = UiKit.Muted;
            _detail.AppendText(ElementName(element.Element.Id) + " — " + PrevalidationService.StatusText(element) + "\n");
            foreach (var requirement in element.Requirements.OrderBy(r => r.Status == CheckStatus.Fail ? 0 : r.Status == CheckStatus.Unknown && !r.ByExporter ? 1 : r.Status == CheckStatus.Unknown ? 2 : 3))
            {
                bool pass = requirement.Status == CheckStatus.Pass, fail = requirement.Status == CheckStatus.Fail;
                _detail.SelectionColor = pass ? UiKit.Done : fail ? UiKit.Pending : requirement.ByExporter ? UiKit.Muted : UiKit.Warning;
                _detail.AppendText(pass ? "✓ " : fail ? "✗ " : "? ");
                _detail.SelectionColor = SystemColors.ControlText;
                _detail.AppendText(requirement + "\n");
            }

            _detail.SelectionStart = 0;
            _detail.ScrollToCaret();
        }

        protected override IReadOnlyCollection<ElementId> SelectedIds() =>
            _elements.SelectedRows.Cast<DataGridViewRow>().Where(r => ((string)r.Tag!).Length > 0)
                .Select(r => Adapter.ModelReader.ElementIdOf((string)r.Tag!)).Distinct().ToList();

        protected override void SelectInGrid(ISet<long> ids)
        {
            _filling = true;
            try
            {
                foreach (DataGridViewRow row in _elements.Rows)
                {
                    string tag = (string)row.Tag!;
                    row.Selected = tag.Length > 0 && char.IsDigit(tag[0]) && ids.Contains(Adapter.ModelReader.ElementIdOf(tag).IdValue());
                }
            }
            finally
            {
                _filling = false;
            }

            ShowDetail();
        }
    }
}
