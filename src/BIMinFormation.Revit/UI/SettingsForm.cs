using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using BIMinFormation.Core.Profile;
using BIMinFormation.Exporter;
using ComboBox = System.Windows.Forms.ComboBox;
using Form = System.Windows.Forms.Form;
using Panel = System.Windows.Forms.Panel;
using TextBox = System.Windows.Forms.TextBox;
using BIMinFormation.Ids;

namespace BIMinFormation.Revit.UI
{
    /// <summary>
    /// Settings window: folder and active .ids files, target schema, parameter file, language, standalone PropertySets file
    /// and parameter cleanup; imports and exports the profile as JSON. "Save to project" writes the profile to the .rvt
    /// (without saving the file). The plugin only reads the .ids files.
    /// </summary>
    internal sealed class SettingsForm : Form
    {
        private readonly ExporterCatalog _exporter;
        private readonly TextBox _folder = new TextBox { Dock = DockStyle.Fill };
        private readonly CheckedListBox _files = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
        private readonly ComboBox _schema = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = UiKit.Px(200) };
        private readonly TextBox _parameters = new TextBox { Dock = DockStyle.Fill };
        private readonly CheckBox _mapAll = new CheckBox
        {
            Text = Tr.T("Mapear todas as fichas da pasta (por padrão, só as fichas em uso no modelo)"),
            AutoSize = true,
        };
        private readonly CheckBox _hide = new CheckBox { Text = Tr.T("Esconder no Preencher as exigências sem valor a preencher"), AutoSize = true };
        private readonly Label _status = new Label { Dock = DockStyle.Fill, AutoEllipsis = false };

        // interface language: a user preference, not a project one
        private readonly ComboBox _language = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = UiKit.Px(260) };
        private static readonly string[] Languages = { PluginSettings.Auto, Tr.Portuguese, Tr.English };

        /// <summary>Language choice: <c>auto</c>, <c>pt-BR</c> or <c>en</c>.</summary>
        public string LanguageChoice => Languages[Math.Max(0, _language.SelectedIndex)];

        private bool _loading;

        public ProjectProfile Profile { get; private set; }

        /// <summary>The user asked for the parameter cleanup (the window closes and the command opens the list).</summary>
        public bool CleanupRequested { get; private set; }

        /// <summary>The user asked for the standalone PropertySets file (the window closes and the command asks where to save).</summary>
        public bool PropertySetsFileRequested { get; private set; }

        public SettingsForm(ProjectProfile profile, ExporterCatalog exporter, string language = PluginSettings.Auto)
        {
            // language names always in their own language, so that whoever does not read the other one finds theirs
            _language.Items.AddRange(new object[] { Tr.T("Automático (idioma do Revit)"), "Português (Brasil)", "English" });
            _language.SelectedIndex = Math.Max(0, Array.IndexOf(Languages, language));
            Profile = ProjectProfile.FromJson(profile.ToJson());
            _exporter = exporter;
            UiKit.Setup(this, Tr.T("BIMinFormation — Configurações"), new Size(820, 640));

            _schema.Items.AddRange(new object[] { Tr.T("Automático (dos .ids)"), "IFC2X3", "IFC4", "IFC4X3_ADD2" });

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(UiKit.Px(10)) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiKit.Px(150)));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiKit.Px(130)));

            void Row(string label, Control control, Control? extra = null, float height = 32)
            {
                layout.RowStyles.Add(height > 0 ? new RowStyle(SizeType.Absolute, UiKit.Px((int)height)) : new RowStyle(SizeType.Percent, 100));
                layout.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft });
                layout.Controls.Add(control);
                layout.Controls.Add(extra ?? new Label());
            }

            Row(Tr.T("Pasta dos .ids:"), _folder, UiKit.Button(Tr.T("Selecionar..."), (s, e) => PickFolder()));
            var checkButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            checkButtons.Controls.Add(UiKit.Button(Tr.T("Marcar todos"), (s, e) => CheckAll(true)));
            checkButtons.Controls.Add(UiKit.Button(Tr.T("Desmarcar todos"), (s, e) => CheckAll(false)));
            Row(Tr.T("Arquivos ativos:"), _files, checkButtons, 0);
            Row(Tr.T("Esquema IFC alvo:"), _schema);
            Row(Tr.T("Parâmetros:"), _parameters, UiKit.Button(Tr.T("Selecionar..."), (s, e) => PickParameters()));
            Row("", _hide);
            Row("", _mapAll);
            Row(Tr.T("Idioma:"), _language);
            var propertySets = UiKit.Button(Tr.T("Salvar..."), (s, e) =>
            {
                PropertySetsFileRequested = true;
                DialogResult = DialogResult.Cancel;
                Close();
            });
            new ToolTip().SetToolTip(propertySets, Tr.T("Gera o arquivo de PropertySets das fichas em uso, com os avisos e as opções de exportação."));
            Row("PropertySets:", new Label
            {
                Text = Tr.T("Arquivo avulso, para exportar sem o botão Exportar IFC."),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = UiKit.Muted,
            }, propertySets);
            Row("", new Label
            {
                Text = Tr.T("O .rvt não é salvo: o perfil entra no modelo e você salva quando quiser."),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = UiKit.Muted,
            }, null, 26);
            Row(Tr.T("Situação:"), _status, null, 110);

            var import = UiKit.Button(Tr.T("Importar perfil..."), (s, e) => Import(), 130);
            var export = UiKit.Button(Tr.T("Exportar perfil..."), (s, e) => Export(), 130);
            var save = UiKit.Button(Tr.T("Salvar no projeto"), (s, e) => Save(), 140);
            var cleanup = UiKit.Button(Tr.T("Limpar parâmetros..."), (s, e) =>
            {
                CleanupRequested = true;
                DialogResult = DialogResult.Cancel;
                Close();
            }, 150);
            new ToolTip().SetToolTip(cleanup, Tr.T("Apaga do projeto os parâmetros do plugin que o mapeamento atual não usa."));
            var close = UiKit.Button(Tr.T("Fechar"), (s, e) => Close());
            Controls.Add(layout);
            Controls.Add(UiKit.Footer(new Label(), cleanup,
                import, export, save, close));
            CancelButton = close;

            _folder.Leave += (s, e) => LoadFiles();
            _files.ItemCheck += (s, e) =>
            {
                // the new state only holds after the event; before the window exists there is no message queue
                if (_loading) return;
                if (IsHandleCreated) BeginInvoke(new Action(Evaluate));
            };
            _schema.SelectedIndexChanged += (s, e) => Evaluate();
            Show(Profile);
        }

        private void Show(ProjectProfile profile)
        {
            _loading = true;
            _folder.Text = profile.IdsFolder ?? "";
            _parameters.Text = profile.ParametersFile ?? "";
            _hide.Checked = profile.HideWhenNoValue;
            _mapAll.Checked = profile.MapAllSpecifications;
            _schema.SelectedItem = profile.TargetSchema ?? Tr.T("Automático (dos .ids)");
            if (_schema.SelectedIndex < 0) _schema.SelectedIndex = 0;
            _loading = false;
            LoadFiles();
        }

        private void LoadFiles()
        {
            _loading = true;
            _files.Items.Clear();
            string folder = _folder.Text.Trim();
            if (Directory.Exists(folder))
            {
                var active = new HashSet<string>(Profile.IdsFiles, StringComparer.OrdinalIgnoreCase);
                foreach (string file in Directory.EnumerateFiles(folder, "*.ids", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    string relative = file.Substring(folder.TrimEnd('\\').Length + 1);
                    _files.Items.Add(relative, active.Count == 0 || active.Contains(relative));
                }
            }

            _loading = false;
            Evaluate();
        }

        private void CheckAll(bool value)
        {
            for (int i = 0; i < _files.Items.Count; i++) _files.SetItemChecked(i, value);
        }

        /// <summary>Copies the screen to the profile (all checked = empty list = every file in the folder).</summary>
        private ProjectProfile Collect()
        {
            var profile = ProjectProfile.FromJson(Profile.ToJson());
            profile.IdsFolder = _folder.Text.Trim().Length == 0 ? null : _folder.Text.Trim();
            var checkedFiles = _files.CheckedItems.Cast<string>().ToList();
            profile.IdsFiles = checkedFiles.Count == _files.Items.Count ? new List<string>() : checkedFiles;
            profile.TargetSchema = _schema.SelectedIndex <= 0 ? null : _schema.SelectedItem as string;
            profile.ParametersFile = _parameters.Text.Trim().Length == 0 ? null : _parameters.Text.Trim();
            profile.HideWhenNoValue = _hide.Checked;
            profile.MapAllSpecifications = _mapAll.Checked;
            return profile;
        }

        private void Evaluate()
        {
            if (_loading) return;
            Cursor = Cursors.WaitCursor;
            try
            {
                var status = ProfileStatus.Evaluate(Collect(), _exporter);
                _status.Text = status.Summary + (status.Messages.Count > 0 ? "\n• " + string.Join("\n• ", status.Messages) : "");
                _status.ForeColor = status.Level == StatusLevel.Ok ? UiKit.Done : status.Level == StatusLevel.Warning ? UiKit.Warning : UiKit.Pending;
            }
            catch (Exception ex)
            {
                _status.Text = Tr.T("Falha ao ler os .ids: ") + ex.Message;
                _status.ForeColor = UiKit.Pending;
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void PickFolder()
        {
            using var picker = new FolderBrowserDialog
            {
                Description = Tr.T("Pasta com os arquivos .ids recebidos (lida recursivamente)"),
                SelectedPath = _folder.Text,
                ShowNewFolderButton = false,
            };
            if (picker.ShowDialog(this) == DialogResult.OK)
            {
                _folder.Text = picker.SelectedPath;
                Profile.IdsFiles = new List<string>();
                LoadFiles();
            }
        }

        private void PickParameters()
        {
            using var picker = new SaveFileDialog
            {
                Title = Tr.T("Arquivo de parâmetros compartilhados do plugin"),
                Filter = Tr.T("Parâmetros compartilhados (*.txt)|*.txt"),
                FileName = _parameters.Text.Length > 0 ? Path.GetFileName(_parameters.Text) : "BIMinFormation-parametros.txt",
                OverwritePrompt = false,
            };
            if (picker.ShowDialog(this) == DialogResult.OK)
            {
                _parameters.Text = picker.FileName;
            }
        }

        private void Import()
        {
            using var picker = new OpenFileDialog { Filter = Tr.T("Perfil do BIMinFormation (*.json)|*.json") };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                Profile = ProjectProfile.FromJson(File.ReadAllText(picker.FileName));
                Show(Profile);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, Tr.T("Perfil inválido: ") + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void Export()
        {
            using var picker = new SaveFileDialog { Filter = Tr.T("Perfil do BIMinFormation (*.json)|*.json"), FileName = "BIMinFormation-perfil.json" };
            if (picker.ShowDialog(this) == DialogResult.OK)
            {
                File.WriteAllText(picker.FileName, Collect().ToJson(), new System.Text.UTF8Encoding(false));
            }
        }

        private void Save()
        {
            // nothing checked would be saved as an empty list, which means "every file in the folder"
            if (_files.Items.Count > 0 && _files.CheckedItems.Count == 0)
            {
                MessageBox.Show(this, Tr.T("Marque pelo menos um arquivo .ids."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Profile = Collect();
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
