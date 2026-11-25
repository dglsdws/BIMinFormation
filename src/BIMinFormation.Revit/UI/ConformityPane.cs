using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.Revit.UI;
using BIMinFormation.Core.Prevalidation;
using WpfButton = System.Windows.Controls.Button;
using WpfColor = System.Windows.Media.Color;
using WpfListView = System.Windows.Controls.ListView;
using WpfProgressBar = System.Windows.Controls.ProgressBar;
using WpfTextBlock = System.Windows.Controls.TextBlock;
using BIMinFormation.Ids;

namespace BIMinFormation.Revit.UI
{
    /// <summary>Panel row: a specification with elements.</summary>
    public sealed class ConformityRow
    {
        public string Name { get; set; } = "";
        public int Total { get; set; }
        public int Ok { get; set; }
        public int Failed { get; set; }
        public int ToCheck { get; set; }
        public string OkText => Ok + Tr.T(" de ") + Total;
    }

    /// <summary>
    /// Dockable conformity panel: the pre-validation summarized next to the view while modeling. Specifications with
    /// elements (failures first), totals and progress; double-click opens the full pre-validation on the specification.
    /// Revit only accepts WPF in a dockable pane; the content is built in code.
    /// </summary>
    public sealed class ConformityPane : UserControl, IDockablePaneProvider
    {
        private readonly WpfTextBlock _title = new WpfTextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap };
        private readonly WpfTextBlock _summary = new WpfTextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
        private readonly WpfProgressBar _progress = new WpfProgressBar { Height = 10, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 0, 0, 6) };
        private readonly WpfListView _list = new WpfListView();
        private readonly WpfTextBlock _note = new WpfTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(WpfColor.FromRgb(120, 120, 120)),
            Margin = new Thickness(0, 4, 0, 0),
            Text = Tr.T("Atualiza alguns segundos depois de cada alteração. Duplo clique abre a pré-validação na ficha."),
        };

        /// <summary>Request to open the pre-validation (chosen specification, or <c>null</c>); handled in Revit Idling.</summary>
        public event Action<string?>? OpenRequested;

        internal int RowCount => _list.Items.Count;
        internal string SummaryText => _summary.Text;

        public ConformityPane()
        {
            var view = new GridView();
            view.Columns.Add(Column(Tr.T("Ficha"), nameof(ConformityRow.Name), 190));
            view.Columns.Add(Column(Tr.T("Ok"), nameof(ConformityRow.OkText), 60));
            view.Columns.Add(Column(Tr.T("Falham"), nameof(ConformityRow.Failed), 52));
            view.Columns.Add(Column("A conferir", nameof(ConformityRow.ToCheck), 64));
            _list.View = view;
            _list.MouseDoubleClick += (s, e) => OpenRequested?.Invoke((_list.SelectedItem as ConformityRow)?.Name);

            // no Refresh button: the panel follows the model changes by itself (PaneController)
            var open = new WpfButton { Content = Tr.T("Abrir pré-validação"), Padding = new Thickness(8, 2, 8, 2) };
            open.Click += (s, e) => OpenRequested?.Invoke((_list.SelectedItem as ConformityRow)?.Name);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            buttons.Children.Add(open);

            var root = new DockPanel { Margin = new Thickness(8), LastChildFill = true };
            foreach (var top in new UIElement[] { _title, _summary, _progress })
            {
                DockPanel.SetDock(top, Dock.Top);
                root.Children.Add(top);
            }

            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            DockPanel.SetDock(_note, Dock.Bottom);
            root.Children.Add(_note);
            root.Children.Add(_list);
            Content = root;
            Show("BIMinFormation", null, Tr.T("Abra um projeto com o perfil configurado."));
        }

        private static GridViewColumn Column(string header, string path, double width) =>
            new GridViewColumn { Header = header, Width = width, DisplayMemberBinding = new System.Windows.Data.Binding(path) };

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = this;
            data.InitialState = new DockablePaneState { DockPosition = DockPosition.Right };
            data.VisibleByDefault = false; // closed until the user opens it with the Panel button
        }

        /// <summary>Shows the pre-validation result (or only the message, without a result).</summary>
        public void Show(string title, PrevalidationResult? result, string? message)
        {
            _title.Text = title;
            if (result == null)
            {
                _summary.Text = message ?? "";
                _progress.Value = 0;
                _list.ItemsSource = null;
                return;
            }

            var rows = result.Specifications.Where(s => s.Elements.Count > 0)
                .Select(s => new ConformityRow { Name = s.Specification.Name, Total = s.Elements.Count, Ok = s.Ok, Failed = s.Failed, ToCheck = s.ToCheck })
                .OrderByDescending(r => r.Failed > 0).ThenByDescending(r => r.ToCheck > 0)
                .ThenBy(r => r.Name, Core.Classification.PortugueseComparer.Instance)
                .ToList();
            int total = rows.Sum(r => r.Total);
            int ok = rows.Sum(r => r.Ok);
            _summary.Text = total == 0
                ? Tr.T("Nenhum elemento com ficha ainda. Classifique os elementos para acompanhar aqui.")
                : "Ok " + ok + Tr.T(" de ") + total + Tr.T(" · falham ") + rows.Sum(r => r.Failed) + Tr.T(" · a conferir ") + rows.Sum(r => r.ToCheck)
                  + (result.Orphans.Count > 0 ? Tr.T(" · identidade sem ficha ") + result.Orphans.Count : "");
            _progress.Value = total == 0 ? 0 : (double)ok / total;
            _list.ItemsSource = rows;
        }
    }
}
