using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using BIMinFormation.Core.Classification;
using BIMinFormation.Ids;

namespace BIMinFormation.Revit.UI
{
    /// <summary>
    /// Specification picker: modal, search ignoring accents and case with several terms, list with every specification,
    /// OK / Cancel / Clear. When the specification gives a list of predefined types, the choice is made here.
    /// </summary>
    internal sealed class SpecPickerForm : Form
    {
        private readonly IReadOnlyList<Specification> _all;
        private readonly IfcSchemaVersion _schema;
        private readonly TextBox _search = new TextBox { Dock = DockStyle.Fill };
        private readonly Label _count = new Label { Dock = DockStyle.Right, Width = UiKit.TextWidth(Tr.T("0000 de 0000 fichas")), TextAlign = ContentAlignment.MiddleRight };
        private readonly ListView _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false,
            ShowItemToolTips = true,
        };
        private readonly ComboBox _predefined = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = UiKit.Px(240), Enabled = false };
        private readonly Label _detail = new Label { Dock = DockStyle.Bottom, Height = UiKit.Px(40), ForeColor = UiKit.Muted };

        /// <summary>Chosen specification (with the predefined type, if the specification asks for a choice); <c>null</c> with Clear.</summary>
        public ClassificationAction? Result { get; private set; }

        public bool Cleared { get; private set; }

        /// <summary>For the tests inside Revit.</summary>
        internal int ListCount => _list.Items.Count;

        internal void Search(string text) => _search.Text = text;

        public SpecPickerForm(IReadOnlyList<Specification> specifications, IfcSchemaVersion schema, int rows, Specification? current)
        {
            _all = specifications;
            _schema = schema;
            UiKit.Setup(this, Tr.T("BIMinFormation — Escolher ficha"), new Size(900, 600));

            _list.Columns.Add(Tr.T("Ficha"), UiKit.Px(330));
            _list.Columns.Add(Tr.T("Entidade"), UiKit.Px(180));
            _list.Columns.Add("Predefined", UiKit.Px(160));
            _list.Columns.Add(Tr.T("Arquivo"), UiKit.Px(180));

            var top = new Panel { Dock = DockStyle.Top, Height = UiKit.Px(34), Padding = UiKit.Px(8, 6, 8, 2) };
            top.Controls.Add(_search);
            top.Controls.Add(_count);
            top.Controls.Add(new Label { Text = Tr.T("Pesquisar:"), Dock = DockStyle.Left, Width = UiKit.TextWidth(Tr.T("Pesquisar:")), TextAlign = ContentAlignment.MiddleLeft });

            var predefinedPanel = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = UiKit.Px(34), Padding = UiKit.Px(8, 4, 8, 0) };
            predefinedPanel.Controls.Add(new Label { Text = Tr.T("Tipo predefinido:"), AutoSize = true, Padding = UiKit.Px(0, 6, 0, 0) });
            predefinedPanel.Controls.Add(_predefined);

            var ok = UiKit.Button(Tr.T("OK"), (s, e) => Confirm());
            var cancel = UiKit.Button(Tr.T("Cancelar"), (s, e) => Close());
            var clear = UiKit.Button(Tr.T("Limpar"), (s, e) => { Cleared = true; DialogResult = DialogResult.OK; Close(); });
            var footer = UiKit.Footer(new Label { Text = rows > 1 ? Tr.T("Aplicar a ") + rows + Tr.T(" linhas selecionadas") : "" }, clear, ok, cancel);
            AcceptButton = ok;
            CancelButton = cancel;

            var body = new Panel { Dock = DockStyle.Fill, Padding = UiKit.Px(8, 0, 8, 0) };
            body.Controls.Add(_list);
            body.Controls.Add(_detail);
            Controls.Add(body);
            Controls.Add(predefinedPanel);
            Controls.Add(footer);
            Controls.Add(top);

            _search.TextChanged += (s, e) => Fill();
            _search.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Down && _list.Items.Count > 0)
                {
                    _list.Focus();
                    _list.Items[0].Selected = true;
                    e.Handled = true;
                }
            };
            _list.SelectedIndexChanged += (s, e) => Selected();
            _list.DoubleClick += (s, e) => Confirm();
            Fill();
            if (current != null)
            {
                var item = _list.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Tag == current);
                if (item != null)
                {
                    item.Selected = true;
                    item.EnsureVisible();
                }
            }

            Shown += (s, e) => _search.Focus();
        }

        private static string Normalize(string text) => PortugueseComparer.RemoveAccents(text).ToLowerInvariant();

        private void Fill()
        {
            string[] terms = Normalize(_search.Text).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var specification in _all)
            {
                var entity = specification.Requirements.Facets.Concat(specification.Applicability.Facets).OfType<EntityFacet>().FirstOrDefault();
                string entityText = Describe(entity?.Name);
                string predefinedText = Describe(entity?.PredefinedType);
                string file = specification.SourceFile == null ? "" : Path.GetFileName(specification.SourceFile);
                string haystack = Normalize(specification.Name + " " + entityText + " " + predefinedText + " " + file);
                if (terms.Any(t => !haystack.Contains(t)))
                {
                    continue;
                }

                var item = new ListViewItem(new[] { specification.Name, entityText, predefinedText, file })
                {
                    Tag = specification,
                    ToolTipText = specification.Description ?? specification.Instructions ?? specification.Name,
                };
                _list.Items.Add(item);
            }

            _list.EndUpdate();
            _count.Text = _list.Items.Count + Tr.T(" de ") + _all.Count + Tr.T(" fichas");
        }

        private static string Describe(IdsValue? value) =>
            value switch
            {
                SimpleValue simple => simple.Text,
                RestrictionValue restriction => restriction.ClosedList() is { } list ? string.Join(" | ", list) : Tr.T("(padrão)"),
                _ => "",
            };

        private void Selected()
        {
            _predefined.Items.Clear();
            _predefined.Enabled = false;
            if (_list.SelectedItems.Count == 0)
            {
                _detail.Text = "";
                return;
            }

            var specification = (Specification)_list.SelectedItems[0].Tag!;
            var action = ClassificationAction.For(specification, _schema);
            _detail.Text = (specification.Description ?? "") + (action.Problems.Count > 0 ? "  " + string.Join(" ", action.Problems) : "");
            if (action.PredefinedTypeChoices != null)
            {
                _predefined.Items.AddRange(action.PredefinedTypeChoices.Cast<object>().ToArray());
                _predefined.Enabled = true;
                _predefined.SelectedIndex = 0;
            }
        }

        private void Confirm()
        {
            if (_list.SelectedItems.Count == 0)
            {
                return;
            }

            var action = ClassificationAction.For((Specification)_list.SelectedItems[0].Tag!, _schema);
            if (action.NeedsPredefinedChoice && _predefined.SelectedItem is string choice)
            {
                action = action.WithPredefinedType(choice);
            }

            Result = action;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
