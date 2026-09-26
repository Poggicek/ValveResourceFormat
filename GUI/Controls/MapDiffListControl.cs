using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using GUI.Utils;
using ValveResourceFormat.Renderer.World.Diff;

namespace GUI.Controls
{
    /// <summary>
    /// Lists the differences between two builds of a map, with filters, and the values that changed for the one
    /// selected. Selecting one raises <see cref="EntrySelected"/>.
    /// </summary>
    internal sealed class MapDiffListControl : UserControl
    {
        private readonly MapDiffResult result;
        private readonly BufferedDataGridView changesGrid;
        private readonly BufferedDataGridView detailsGrid;
        private readonly ThemedTextBox filterTextBox;
        private readonly Dictionary<MapDiffKind, CheckBox> kindFilters = [];
        private readonly Dictionary<MapDiffCategory, CheckBox> categoryFilters = [];
        private bool rebuilding;
        private MapDiffEntry? selectedEntry;

        /// <summary>Raised when an entry is selected, by clicking it or stepping to it.</summary>
        public event EventHandler<MapDiffEntry>? EntrySelected;

        public MapDiffListControl(MapDiffResult result, string oldName, string newName)
        {
            this.result = result;

            SuspendLayout();

            var summaryLabel = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = this.AdjustForDPI(40),
                Padding = new Padding(4, 4, 4, 0),
                Text = Summary(oldName, newName),
            };

            filterTextBox = new ThemedTextBox
            {
                Dock = DockStyle.Top,
                PlaceholderText = "Filter by class, name or detail",
            };
            filterTextBox.TextChanged += (_, _) => Rebuild();

            var filtersPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true,
                Padding = new Padding(2),
            };

            foreach (var kind in Enum.GetValues<MapDiffKind>())
            {
                kindFilters[kind] = AddFilter(filtersPanel, $"{kind} ({result.Entries.Count(entry => entry.Kind == kind)})");
            }

            foreach (var category in Enum.GetValues<MapDiffCategory>())
            {
                var count = result.Entries.Count(entry => entry.Category == category);
                var label = category switch
                {
                    MapDiffCategory.Entity => "Entities",
                    MapDiffCategory.Collision => "Collision",
                    _ => "Geometry",
                };

                categoryFilters[category] = AddFilter(filtersPanel, $"{label} ({count})");
            }

            changesGrid = CreateGrid();
            changesGrid.Columns.Add("Change", "Change");
            changesGrid.Columns.Add("Type", "Type");
            changesGrid.Columns.Add("Name", "Name");
            changesGrid.Columns.Add("Detail", "Detail");
            changesGrid.Columns[0].FillWeight = 20;
            changesGrid.Columns[1].FillWeight = 32;
            changesGrid.Columns[2].FillWeight = 40;
            changesGrid.Columns[3].FillWeight = 34;
            changesGrid.SelectionChanged += OnChangeSelected;

            detailsGrid = CreateGrid();
            detailsGrid.Columns.Add("Key", "Key");
            detailsGrid.Columns.Add("Old", "Old build");
            detailsGrid.Columns.Add("New", "New build");
            detailsGrid.Columns[0].FillWeight = 60;
            detailsGrid.ShowCellToolTips = true;

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterWidth = this.AdjustForDPI(4),
            };
            split.Panel1.Controls.Add(changesGrid);
            split.Panel2.Controls.Add(detailsGrid);

            Controls.Add(split);
            Controls.Add(filtersPanel);
            Controls.Add(filterTextBox);
            Controls.Add(summaryLabel);

            Themer.ThemeControl(this);

            ResumeLayout();

            Load += (_, _) => split.SplitterDistance = (int)(split.Height * 0.62f);

            Rebuild();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                changesGrid.Dispose();
                detailsGrid.Dispose();
                filterTextBox.Dispose();
            }

            base.Dispose(disposing);
        }

        private string Summary(string oldName, string newName)
        {
            var entries = result.Entries;

            if (entries.Count == 0)
            {
                return $"{oldName} → {newName}: no differences found.";
            }

            return $"{oldName} → {newName}: {entries.Count} differences, "
                + $"{entries.Count(static entry => entry.Category == MapDiffCategory.Entity)} in entities, "
                + $"{entries.Count(static entry => entry.Category == MapDiffCategory.Geometry)} in geometry "
                + $"and {entries.Count(static entry => entry.Category == MapDiffCategory.Collision)} in collision.";
        }

        private CheckBox AddFilter(FlowLayoutPanel panel, string text)
        {
            var checkBox = new CheckBox
            {
                Text = text,
                Checked = true,
                AutoSize = true,
            };

            checkBox.CheckedChanged += (_, _) => Rebuild();
            panel.Controls.Add(checkBox);

            return checkBox;
        }

        /// <summary>
        /// A grid that paints into a back buffer. Drawing straight to the window is slow while the world is being
        /// rendered next to it, as every line and string waits on the GPU; scrolling took ~55 ms per step.
        /// </summary>
        private sealed class BufferedDataGridView : DataGridView
        {
            public BufferedDataGridView()
            {
                DoubleBuffered = true;
            }
        }

        private static BufferedDataGridView CreateGrid()
        {
            var grid = new BufferedDataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            };

            return grid;
        }

        /// <summary>Gets the color changes of a kind are marked with.</summary>
        public static Color KindColor(MapDiffKind kind) => kind switch
        {
            MapDiffKind.Added => Color.FromArgb(70, 200, 90),
            MapDiffKind.Removed => Color.FromArgb(230, 75, 75),
            MapDiffKind.Moved => Color.FromArgb(80, 160, 255),
            _ => Color.FromArgb(225, 175, 30),
        };

        private void Rebuild()
        {
            var filter = filterTextBox.Text.Trim();
            var selected = selectedEntry;

            rebuilding = true;
            changesGrid.SuspendLayout();
            changesGrid.Rows.Clear();

            foreach (var entry in result.Entries)
            {
                if (!kindFilters[entry.Kind].Checked || !categoryFilters[entry.Category].Checked)
                {
                    continue;
                }

                if (filter.Length > 0
                    && !entry.Type.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    && !entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    && !entry.Detail.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var index = changesGrid.Rows.Add(entry.Kind.ToString(), entry.Type, entry.Name, entry.Detail);
                var row = changesGrid.Rows[index];
                row.Tag = entry;
                row.Cells[0].Style.ForeColor = KindColor(entry.Kind);
                row.Cells[0].Style.SelectionForeColor = KindColor(entry.Kind);
            }

            changesGrid.ResumeLayout();
            changesGrid.ClearSelection();
            selectedEntry = null;
            rebuilding = false;

            if (selected != null)
            {
                foreach (DataGridViewRow row in changesGrid.Rows)
                {
                    if (row.Tag == selected)
                    {
                        rebuilding = true;
                        changesGrid.CurrentCell = row.Cells[0];
                        selectedEntry = selected;
                        rebuilding = false;
                        break;
                    }
                }
            }
        }

        private void OnChangeSelected(object? sender, EventArgs e)
        {
            // CurrentRow still names the previous row while the selection changes, SelectedRows does not
            if (rebuilding || changesGrid.SelectedRows.Count == 0 || changesGrid.SelectedRows[0].Tag is not MapDiffEntry entry || entry == selectedEntry)
            {
                return;
            }

            selectedEntry = entry;
            ShowDetails(entry);
            EntrySelected?.Invoke(this, entry);
        }

        private void ShowDetails(MapDiffEntry entry)
        {
            detailsGrid.SuspendLayout();
            detailsGrid.Rows.Clear();

            detailsGrid.Rows.Add("position",
                entry.OldBounds is { } oldBounds ? FormatVector(oldBounds.Center) : string.Empty,
                entry.NewBounds is { } newBounds ? FormatVector(newBounds.Center) : string.Empty);

            foreach (var change in entry.Changes)
            {
                var index = detailsGrid.Rows.Add(change.Key, change.OldValue ?? string.Empty, change.NewValue ?? string.Empty);
                detailsGrid.Rows[index].Cells[1].ToolTipText = change.OldValue ?? string.Empty;
                detailsGrid.Rows[index].Cells[2].ToolTipText = change.NewValue ?? string.Empty;
            }

            detailsGrid.ResumeLayout();
            detailsGrid.ClearSelection();

            static string FormatVector(Vector3 vector) => string.Create(CultureInfo.InvariantCulture, $"{vector.X:0} {vector.Y:0} {vector.Z:0}");
        }

        /// <summary>Selects the entry <paramref name="step"/> rows away from the selected one, wrapping around.</summary>
        public void SelectRelative(int step)
        {
            var count = changesGrid.Rows.Count;

            if (count == 0)
            {
                return;
            }

            var current = changesGrid.SelectedRows.Count > 0 ? changesGrid.SelectedRows[0].Index : (step > 0 ? -1 : 0);
            var next = ((current + step) % count + count) % count;

            changesGrid.CurrentCell = changesGrid.Rows[next].Cells[0];
        }
    }
}
