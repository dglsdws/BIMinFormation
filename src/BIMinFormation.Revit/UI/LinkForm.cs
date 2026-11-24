using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using BIMinFormation.Core.Profile;
using BIMinFormation.Revit.Adapter;
using Form = System.Windows.Forms.Form;
using Panel = System.Windows.Forms.Panel;
using BIMinFormation.Ids;

namespace BIMinFormation.Revit.UI
{
    /// <summary>
    /// Links an .ids property to a parameter Revit already has. Shows the element and type parameters that suit the
    /// property, with the value as it comes out in the IFC; the one with the most similar name is suggested, and it only
    /// applies once confirmed. For material properties it offers the element material calculated by the plugin.
    /// </summary>
    internal sealed class LinkForm : Form
    {
        private readonly ListView _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
        };

        private readonly CheckBox _onlyEntity = new CheckBox { AutoSize = true };
        private readonly Button _link;

        /// <summary>Chosen parameter (name, or <see cref="ParameterMapping.DerivedMaterial"/>); <c>null</c> = no link.</summary>
        public string? Chosen { get; private set; }

        /// <summary>Applies only to the element's entity (otherwise to every entity).</summary>
        public bool OnlyEntity => _onlyEntity.Checked;

        /// <summary>The chosen parameter, with the identifier that does not change with the language; <c>null</c> for the calculated material.</summary>
        public LinkCandidate? ChosenCandidate => Chosen == null ? null : _candidates.FirstOrDefault(c => c.Name == Chosen);

        private readonly IReadOnlyList<LinkCandidate> _candidates;

        internal int CandidateCount => _list.Items.Count;

        internal string? Suggested => _list.SelectedItems.Count == 0 ? _initial : (string)_list.SelectedItems[0].Tag!;

        private readonly string? _initial;

        /// <summary>Selected row in the list (tests).</summary>
        internal string? SelectedName => _list.SelectedItems.Count == 0 ? null : (string)_list.SelectedItems[0].Tag!;

        private void SelectInitial()
        {
            var selected = _list.Items.Cast<ListViewItem>().FirstOrDefault(i => (string)i.Tag! == _initial);
            if (selected == null) return;
            selected.Selected = true;
            selected.Focused = true;
            selected.EnsureVisible();
            _list.Focus();
            _link.Enabled = true;
        }

        public LinkForm(string property, string detail, string entity, IReadOnlyList<LinkCandidate> candidates,
            string? materialPreview, ParameterMapping? current)
        {
            _candidates = candidates;
            UiKit.Setup(this, Tr.T("BIMinFormation — Vincular ao Revit"), new Size(820, 520));
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;

            _list.Columns.Add(Tr.T("Parâmetro do Revit"), UiKit.Px(260));
            _list.Columns.Add(Tr.T("Valor (como sai no IFC)"), UiKit.Px(220));
            _list.Columns.Add(Tr.T("Onde"), UiKit.Px(90));
            _list.Columns.Add(Tr.T("Sugestão"), UiKit.Px(90));

            if (materialPreview != null)
            {
                var item = new ListViewItem(new[] { Tr.T("Material do elemento (todos ou um deles, escolhido no Preencher)"), materialPreview, Tr.T("camadas"), "" })
                {
                    Tag = ParameterMapping.DerivedMaterial,
                };
                _list.Items.Add(item);
            }

            foreach (var candidate in candidates)
            {
                _list.Items.Add(new ListViewItem(new[]
                {
                    candidate.Name,
                    candidate.Value,
                    candidate.OnType ? Tr.T("tipo") : Tr.T("instância"),
                    candidate.Score >= 1 ? "★★★" : candidate.Score >= 0.5 ? "★★" : candidate.Score > 0 ? "★" : "",
                })
                {
                    Tag = candidate.Name,
                });
            }

            // initial selection: the current link, otherwise the best suggestion (a very similar name)
            string? initial = current?.Parameter
                              ?? (materialPreview != null ? ParameterMapping.DerivedMaterial : null)
                              ?? candidates.FirstOrDefault(c => c.Score >= 0.5)?.Name;
            _initial = _list.Items.Cast<ListViewItem>().Any(i => (string)i.Tag! == initial) ? initial : null;
            // the initial selection goes after the window shows: in the list HandleCreated the items have not reached Windows
            // yet (index -1, exception and an empty list)
            Shown += (s, e) => SelectInitial();

            _onlyEntity.Text = Tr.T("Só para ") + entity + Tr.T(" (desmarcado: para todas as entidades com esta propriedade)");
            _onlyEntity.Checked = current == null || current.Entity != null;

            var header = new Label
            {
                Text = property + "\n" + detail,
                Dock = DockStyle.Top,
                Height = UiKit.Px(48),
                Padding = UiKit.Px(8, 6, 8, 0),
            };
            var note = new Label
            {
                Text = Tr.T("O exportador lê o parâmetro vinculado na hora de exportar: o valor fica sempre atual e o plugin não cria parâmetro novo."),
                Dock = DockStyle.Bottom,
                Height = UiKit.Px(40),
                Padding = UiKit.Px(8, 4, 8, 0),
                ForeColor = UiKit.Muted,
            };
            var options = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = UiKit.Px(30), Padding = UiKit.Px(8, 4, 8, 0) };
            options.Controls.Add(_onlyEntity);

            var body = new Panel { Dock = DockStyle.Fill, Padding = UiKit.Px(8, 0, 8, 0) };
            body.Controls.Add(_list);

            _link = UiKit.Button(Tr.T("Vincular"), (s, e) => Confirm());
            var remove = UiKit.Button(Tr.T("Sem vínculo"), (s, e) => { Chosen = null; DialogResult = DialogResult.OK; Close(); }, 120);
            remove.Enabled = current != null;
            var cancel = UiKit.Button(Tr.T("Cancelar"), (s, e) => Close());
            var footer = UiKit.Footer(new Label { Text = candidates.Count + Tr.T(" parâmetro(s) compatível(is)") }, remove, _link, cancel);
            AcceptButton = _link;
            CancelButton = cancel;

            Controls.Add(body);
            Controls.Add(options);
            Controls.Add(note);
            Controls.Add(footer);
            Controls.Add(header);

            _list.SelectedIndexChanged += (s, e) => _link.Enabled = _list.SelectedItems.Count > 0;
            _list.DoubleClick += (s, e) => Confirm();
            _link.Enabled = _initial != null;
        }

        private void Confirm()
        {
            if (_list.SelectedItems.Count == 0) return;
            Chosen = (string)_list.SelectedItems[0].Tag!;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
