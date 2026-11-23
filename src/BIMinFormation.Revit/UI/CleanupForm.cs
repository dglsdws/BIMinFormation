using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using BIMinFormation.Revit.Services;
using Form = System.Windows.Forms.Form;
using BIMinFormation.Ids;

namespace BIMinFormation.Revit.UI
{
    /// <summary>
    /// Clean up plugin parameters: lists the parameters the plugin created, whether the current mapping uses each one and
    /// how many elements have a value. The ones outside the mapping and without values come checked; the ones in the
    /// mapping cannot be deleted.
    /// </summary>
    internal sealed class CleanupForm : Form
    {
        private const int CheckColumn = 0;
        private readonly DataGridView _grid = UiKit.Grid();
        private readonly Button _delete;
        private readonly IReadOnlyList<CleanupCandidate> _candidates;

        /// <summary>The ones checked for deletion.</summary>
        public List<CleanupCandidate> Chosen => _grid.Rows.Cast<DataGridViewRow>()
            .Where(r => (bool)(r.Cells[CheckColumn].Value ?? false)).Select(r => (CleanupCandidate)r.Tag!).ToList();

        public CleanupForm(IReadOnlyList<CleanupCandidate> candidates)
        {
            _candidates = candidates;
            UiKit.Setup(this, Tr.T("BIMinFormation — Limpar parâmetros do plugin"), new Size(820, 520));
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;

            _grid.ReadOnly = false;
            _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = Tr.T("APAGAR"), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 10 });
            _grid.Columns.Add(UiKit.TextColumn(Tr.T("PARÂMETRO"), 46, false));
            _grid.Columns.Add(UiKit.TextColumn(Tr.T("SITUAÇÃO"), 26, false));
            _grid.Columns.Add(UiKit.TextColumn(Tr.T("COM VALOR"), 18, false));
            for (int i = 1; i < _grid.Columns.Count; i++) _grid.Columns[i].ReadOnly = true;

            foreach (var candidate in candidates)
            {
                bool suggested = !candidate.InMapping && candidate.WithValue == 0;
                int index = _grid.Rows.Add(suggested, candidate.Name, candidate.InMapping ? Tr.T("no mapeamento atual") : Tr.T("fora do mapeamento"),
                    candidate.WithValue == 0 ? Tr.T("nenhum") : candidate.WithValue + Tr.T(" elemento(s)"));
                var row = _grid.Rows[index];
                row.Tag = candidate;
                if (candidate.InMapping)
                {
                    row.Cells[CheckColumn].ReadOnly = true;
                    row.DefaultCellStyle.ForeColor = UiKit.Muted;
                }
                else if (candidate.WithValue > 0)
                {
                    row.Cells[3].Style.ForeColor = UiKit.Warning;
                }
            }

            _delete = UiKit.Button(Tr.T("Apagar"), (s, e) => Confirm(), 110);
            var cancel = UiKit.Button(Tr.T("Cancelar"), (s, e) => Close());
            var note = new Label
            {
                Text = candidates.Count == 0
                    ? Tr.T("Nenhum parâmetro do plugin no projeto.")
                    : Tr.T("Apagar remove o parâmetro e os valores dele. Os do mapeamento atual não podem ser apagados."),
            };
            Controls.Add(_grid);
            Controls.Add(UiKit.Footer(note, _delete, cancel));
            CancelButton = cancel;

            _grid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            _grid.CellValueChanged += (s, e) => UpdateButton();
            UpdateButton();
        }

        internal int RowCount => _grid.Rows.Count;

        private void UpdateButton()
        {
            int count = Chosen.Count;
            _delete.Text = Tr.T("Apagar (") + count + ")";
            _delete.Enabled = count > 0;
        }

        private void Confirm()
        {
            var chosen = Chosen;
            int withValues = chosen.Count(c => c.WithValue > 0);
            string message = chosen.Count + Tr.T(" parâmetro(s) serão apagados do projeto")
                             + (withValues > 0 ? ", " + withValues + Tr.T(" deles com valores, que serão perdidos") : "") + Tr.T(". Continuar?");
            if (MessageBox.Show(this, message, Text, MessageBoxButtons.YesNo, withValues > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Question)
                != DialogResult.Yes)
            {
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
