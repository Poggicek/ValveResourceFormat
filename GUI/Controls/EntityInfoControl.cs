using System.Windows.Forms;
using GUI.Utils;
using ValveKeyValue;
using static ValveResourceFormat.ResourceTypes.EntityLump;

namespace GUI.Forms
{
    partial class EntityInfoControl : UserControl
    {
        public DataGridView OutputsGrid => dataGridOutputs;
        public DataGridView InputsGrid => dataGridInputs;

        public EntityInfoControl()
        {
            InitializeComponent();

            components ??= new System.ComponentModel.Container();
            components.Add(tabPageOutputs);
            components.Add(tabPageInputs);
        }

        public EntityInfoControl(VrfGuiContext vrfGuiContext) : this()
        {
            ResourceAddDataGridExternalRef(vrfGuiContext);
        }

        public void ResourceAddDataGridExternalRef(VrfGuiContext vrfGuiContext)
        {
            AddDataGridExternalRefAction(vrfGuiContext, dataGridProperties, ColumnValue.Name);
        }

        public void ShowPropertiesTab()
        {
            tabControl.SelectedIndex = 0;
        }

        private TabPage[] TabPageOrder => [tabPageProperties, tabPageOutputs, tabPageInputs];

        public void ShowPopulatedTabs()
        {
            SetTabVisible(tabPageOutputs, dataGridOutputs.RowCount > 0);
            SetTabVisible(tabPageInputs, dataGridInputs.RowCount > 0);
        }

        private void SetTabVisible(TabPage page, bool shouldShow)
        {
            var isShown = tabControl.TabPages.Contains(page);

            if (shouldShow && !isShown)
            {
                tabControl.TabPages.Insert(GetInsertIndex(page), page);
            }
            else if (!shouldShow && isShown)
            {
                tabControl.TabPages.Remove(page);
            }
        }

        private int GetInsertIndex(TabPage page)
        {
            var targetOrder = Array.IndexOf(TabPageOrder, page);
            var index = 0;

            for (var i = 0; i < targetOrder; i++)
            {
                if (tabControl.TabPages.Contains(TabPageOrder[i]))
                {
                    index++;
                }
            }

            return index;
        }

        public void Clear()
        {
            dataGridProperties.Rows.Clear();
            dataGridOutputs.Rows.Clear();
            dataGridInputs.Rows.Clear();
        }

        public void PopulateFromEntity(Entity entity)
        {
            foreach (var child in entity.Children)
            {
                var resourcePath = ResourcePath(child.Value);
                AddProperty(child.Key, resourcePath ?? StringifyValue(child.Value), resourcePath);
            }

            if (entity.Connections != null)
            {
                foreach (var connection in entity.Connections)
                {
                    AddOutputConnection(connection);
                }
            }
        }
        public void PopulateFromEntity(List<Entity> entities, Entity entity)
        {
            foreach (var child in entity.Children)
            {
                var resourcePath = ResourcePath(child.Value);
                AddProperty(child.Key, resourcePath ?? StringifyValue(child.Value), resourcePath);
            }

            if (entity.Connections != null)
            {
                foreach (var connection in entity.Connections)
                {
                    AddOutputConnection(connection);
                }
            }

            foreach (var connection in entity.GetInputConnections(entities))
            {
                AddInputConnection(connection);
            }
        }

        public void AddProperty(string name, string value, string? externalReference = null)
        {
            var rowIndex = dataGridProperties.Rows.Add([name, value]);

            if (externalReference != null)
            {
                dataGridProperties.Rows[rowIndex].Cells[ColumnValue.Name].Tag = externalReference;
            }
        }

        /// <summary>
        /// The bare text of a string property. The KV3 form a value serializes to carries its quotes
        /// and, for a resource, its type prefix (<c>resource_name:"particles/foo.vpcf"</c>), which is
        /// neither what the grid should show nor a path anything can be looked up by.
        /// </summary>
        private static string? ResourcePath(KVObject value)
            => value.ValueType == KVValueType.String ? (string)value : null;

        public void AddOutputConnection(Connection connectionData)
        {
            var rowIndex = dataGridOutputs.Rows.Add([
                connectionData.OutputName,
                connectionData.TargetName,
                connectionData.InputName,
                connectionData.OverrideParam,
                connectionData.Delay,
                GetStringTimesToFire(connectionData.TimesToFire)
            ]);

            dataGridOutputs.Rows[rowIndex].Tag = connectionData;
        }

        public void AddInputConnection(Connection connectionData)
        {
            var rowIndex = dataGridInputs.Rows.Add([
                connectionData.SourceEntity.TargetName ?? "",
                connectionData.OutputName,
                connectionData.InputName,
                connectionData.OverrideParam,
                connectionData.Delay,
                GetStringTimesToFire(connectionData.TimesToFire)
            ]);

            dataGridInputs.Rows[rowIndex].Tag = connectionData;
        }

        /// <summary>
        /// Adds a button to every output and input row that fires that row's connection, for viewers with a
        /// live entity system to fire it in.
        /// </summary>
        public void AddConnectionTriggerButtons(Action<Connection> onTrigger)
        {
            AddTriggerButtonColumn(dataGridOutputs, onTrigger);
            AddTriggerButtonColumn(dataGridInputs, onTrigger);
        }

        private static void AddTriggerButtonColumn(DataGridView dataGrid, Action<Connection> onTrigger)
        {
            var column = new DataGridViewButtonColumn
            {
                Name = "Trigger",
                HeaderText = string.Empty,
                Text = "Trigger",
                UseColumnTextForButtonValue = true,
                FlatStyle = FlatStyle.Flat,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells,
            };

            dataGrid.Columns.Add(column);

            void OnCellContentClick(object? sender, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex < 0 || e.ColumnIndex != column.Index)
                {
                    return;
                }

                if (dataGrid.Rows[e.RowIndex].Tag is Connection connection)
                {
                    onTrigger(connection);
                }
            }

            void OnDisposed(object? sender, EventArgs e)
            {
                dataGrid.CellContentClick -= OnCellContentClick;
                dataGrid.Disposed -= OnDisposed;
            }

            dataGrid.CellContentClick += OnCellContentClick;
            dataGrid.Disposed += OnDisposed;
        }

        private static string GetStringTimesToFire(int timesToFire)
        {
            return timesToFire switch
            {
                1 => "Only Once",
                >= 2 => $"Only {timesToFire} Times",
                _ => "Infinite",
            };
        }

        private void AddDataGridExternalRefAction(VrfGuiContext vrfGuiContext, DataGridView dataGrid, string columnName)
        {
            void OnCellDoubleClick(object? sender, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex < 0 || sender is not DataGridView grid)
                {
                    return;
                }

                var row = grid.Rows[e.RowIndex];
                var colName = columnName;
                var cell = row.Cells[colName];
                var name = cell.Tag as string ?? (string)cell.Value!;

                var found = Types.Viewers.Resource.OpenExternalReference(vrfGuiContext, name);

                if (found && Parent is Form form)
                {
                    form.Close();
                }
            }

            void OnDisposed(object? sender, EventArgs e)
            {
                dataGrid.CellDoubleClick -= OnCellDoubleClick;
                dataGrid.Disposed -= OnDisposed;
            }

            dataGrid.CellDoubleClick += OnCellDoubleClick;
            dataGrid.Disposed += OnDisposed;
        }
    }
}
