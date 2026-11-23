using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using BIMinFormation.Ids;

namespace BIMinFormation.Revit.UI
{
    /// <summary>Revit window as owner of the plugin windows (they stay on top of it, not of the whole desktop).</summary>
    public sealed class RevitWindow : IWin32Window
    {
        public RevitWindow(IntPtr handle)
        {
            Handle = handle;
        }

        public IntPtr Handle { get; }
    }

    /// <summary>Common look of the windows.</summary>
    internal static class UiKit
    {
        /// <summary>Window font (the tests change it to simulate screens with a larger scale).</summary>
        public static Font Font { get; internal set; } = new Font("Segoe UI", 9f);

        private static readonly float SystemDpi = ReadDpi();

        private static float ReadDpi()
        {
            try
            {
                using var graphics = Graphics.FromHwnd(IntPtr.Zero);
                return graphics.DpiX;
            }
            catch (Exception)
            {
                return 96f;
            }
        }

        /// <summary>
        /// Text scale relative to 100% and 9 pt (125% → 1.25). Revit draws text at the screen scale; fixed sizes (widths,
        /// heights, windows) go through <see cref="Px(int)"/> to follow it.
        /// </summary>
        public static float Scale => SystemDpi / 96f * Font.SizeInPoints / 9f;

        public static int Px(int pixels) => (int)Math.Round(pixels * Scale);

        public static Padding Px(int left, int top, int right, int bottom) => new Padding(Px(left), Px(top), Px(right), Px(bottom));

        /// <summary>Width of the text in the window font, with some margin.</summary>
        public static int TextWidth(string text) => TextRenderer.MeasureText(text, Font).Width + Px(8);
        public static readonly Color Muted = Color.FromArgb(120, 120, 120);
        public static readonly Color Pending = Color.FromArgb(178, 34, 34);
        public static readonly Color Done = Color.FromArgb(34, 120, 60);
        public static readonly Color Warning = Color.FromArgb(176, 112, 0);

        public static void Setup(Form form, string title, Size size)
        {
            form.Text = title;
            form.Font = Font;
            var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 4096, 4096);
            form.Size = new Size(Math.Min(Px(size.Width), area.Width * 95 / 100), Math.Min(Px(size.Height), area.Height * 95 / 100));
            form.MinimumSize = new Size(Math.Min(form.Width, Px(640)), Math.Min(form.Height, Px(400)));
            form.StartPosition = FormStartPosition.CenterParent;
            form.ShowInTaskbar = false;
            form.KeyPreview = true;
        }

        public static DataGridView Grid()
        {
            var grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AutoGenerateColumns = false,
                BackgroundColor = SystemColors.Window,
                BorderStyle = BorderStyle.None,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = true,
                ReadOnly = true,
                ShowCellToolTips = true,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                EnableHeadersVisualStyles = true,
            };
            grid.RowTemplate.Height = Px(24);
            return grid;
        }

        public static DataGridViewTextBoxColumn TextColumn(string header, int fill, bool sortable = true) =>
            new DataGridViewTextBoxColumn
            {
                HeaderText = header,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = fill,
                SortMode = sortable ? DataGridViewColumnSortMode.Programmatic : DataGridViewColumnSortMode.NotSortable,
            };

        public static Button Button(string text, EventHandler click, int width = 110)
        {
            var size = TextRenderer.MeasureText(text, Font);
            var button = new Button
            {
                Text = text,
                AutoSize = false,
                Width = Math.Max(Px(width), size.Width + Px(24)),
                Height = Math.Max(Px(28), size.Height + Px(10)),
                Margin = Px(6, 0, 0, 0),
            };
            button.Click += click;
            return button;
        }

        /// <summary>Bottom bar with the summary on the left and the buttons on the right.</summary>
        public static Panel Footer(Label summary, params Button[] buttons)
        {
            int height = buttons.Length == 0 ? Px(28) : buttons.Max(b => b.Height);
            var panel = new Panel { Dock = DockStyle.Bottom, Height = height + Px(16), Padding = new Padding(Px(8)) };
            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                WrapContents = false,
            };
            flow.Controls.AddRange(buttons);
            summary.Dock = DockStyle.Fill;
            summary.TextAlign = ContentAlignment.MiddleLeft;
            summary.ForeColor = Muted;
            panel.Controls.Add(summary);
            panel.Controls.Add(flow);
            return panel;
        }

        /// <summary>Sort glyph in the header (two states: ascending and descending).</summary>
        public static void ShowSort(DataGridView grid, int column, bool ascending)
        {
            foreach (DataGridViewColumn c in grid.Columns)
            {
                c.HeaderCell.SortGlyphDirection = SortOrder.None;
            }

            if (column >= 0)
            {
                grid.Columns[column].HeaderCell.SortGlyphDirection = ascending ? SortOrder.Ascending : SortOrder.Descending;
            }
        }
    }
}
