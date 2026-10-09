using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using GUI.Controls;
using GUI.Types.GLViewers;
using GUI.Utils;
using ValveResourceFormat.Particles;
using ValveResourceFormat.Particles.Debugging;
using ValveResourceFormat.Renderer.Particles;
using ValveResourceFormat.Serialization.KeyValues;

namespace GUI.Forms;

/// <summary>
/// Steps through a particle system one function at a time: what each one changed, which particles it
/// killed, and which first produced a bad value, with switches to take functions out of the pipeline.
/// </summary>
class ParticleDebuggerForm : ThemedForm
{
    private static readonly (ParticleAnomalies Anomaly, string Label, bool BreakByDefault)[] BreakOptions =
    [
        (ParticleAnomalies.NonFinite, "NaN/Inf", true),
        (ParticleAnomalies.FarAway, "Far away", true),
        (ParticleAnomalies.HugeRadius, "Huge radius", true),
        (ParticleAnomalies.NoLifetime, "No lifetime", false),
        (ParticleAnomalies.ZeroRadius, "Zero radius", false),
        (ParticleAnomalies.Transparent, "Zero alpha", false),
        (ParticleAnomalies.Black, "Black", false),
    ];

    private static readonly (string Label, float Seconds)[] StepSizes =
    [
        ("1/60 s", 1f / 60f),
        ("1/30 s", 1f / 30f),
        ("1/10 s", 0.1f),
        ("1/2 s", 0.5f),
    ];

    private static readonly ParticleFunctionStage[] StageOrder =
    [
        ParticleFunctionStage.PreEmissionOperator,
        ParticleFunctionStage.Emitter,
        ParticleFunctionStage.Initializer,
        ParticleFunctionStage.Operator,
        ParticleFunctionStage.ForceGenerator,
        ParticleFunctionStage.Constraint,
        ParticleFunctionStage.Renderer,
    ];

    private const string StepStartLabel = "Step start";
    private const string ConstantsLabel = "Constants";
    private const string RemoveExpiredLabel = "Remove expired";

    private const ParticleAnomalies InvisibleAnomalies = ParticleAnomalies.ZeroRadius | ParticleAnomalies.Transparent | ParticleAnomalies.Black;

    private readonly GLParticleViewer viewer;
    private readonly ParticleDebugSession session;
    private readonly Timer refreshTimer;
    private readonly ImageList systemImages;
    private readonly Font monospaceFont;

    // Owned by the form's control tree, which disposes them
#pragma warning disable CA2213 // Disposable fields should be disposed
    private readonly ThemedButton pauseButton;
    private readonly ComboBox stepSizeComboBox;
    private readonly Label statusLabel;
    private readonly TreeView systemTree;
    private readonly ListView pipelineList;
    private readonly ListView particleList;
    private readonly ListView inspectorList;
    private readonly Label inspectorLabel;
    private readonly ListView problemList;
    private readonly ListView controlPointList;
    private readonly TextBox functionText;
#pragma warning restore CA2213

    private readonly Color severeBack;
    private readonly Color invisibleBack;
    private readonly Color changedBack;
    private readonly Color errorFore = Color.FromArgb(230, 80, 80);
    private readonly Color mutedFore = Color.FromArgb(140, 140, 140);

    private ParticleSystemSimulation? root;
    private ParticleSystemSimulation? selectedSystem;
    private readonly List<PipelineRow> rows = [];
    private PipelineRow? selectedRow;
    private Particle[] shownParticles = [];
    private Vector3 shownOrigin;
    private int selectedParticleId = -1;
    private ParticleBreak? shownBreak;
    private (int StepNumber, int Version) shownState = (-1, -1);
    private int version;
    private bool updating;

    private sealed class PipelineRow(ParticleFunctionStage? stage, ParticleDebugFunction? function, string label)
    {
        public ParticleFunctionStage? Stage { get; } = stage;
        public ParticleDebugFunction? Function { get; } = function;
        public string Label { get; } = label;
        public ListViewItem Item { get; } = new();

        public bool Matches(ParticleTraceStep step) => Function != null ? step.Function == Function : step.Function == null && step.Label == Label;
    }

    public ParticleDebuggerForm(GLParticleViewer viewer, ParticleDebugSession session)
    {
        this.viewer = viewer;
        this.session = session;

        Text = "Particle Debugger";
        Icon = Program.MainForm.Icon;
        Width = this.AdjustForDPI(1300);
        Height = this.AdjustForDPI(820);

        var dark = Application.IsDarkModeEnabled;
        severeBack = dark ? Color.FromArgb(96, 32, 32) : Color.FromArgb(255, 210, 210);
        invisibleBack = dark ? Color.FromArgb(90, 64, 20) : Color.FromArgb(255, 234, 196);
        changedBack = dark ? Color.FromArgb(32, 64, 96) : Color.FromArgb(214, 232, 255);

        // Toolbar
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true,
            Padding = new Padding(4),
        };

        pauseButton = new ThemedButton { Text = "Pause", AutoSize = true };
        pauseButton.Click += (_, _) => TogglePause();

        var stepButton = new ThemedButton { Text = "Step", AutoSize = true };
        stepButton.Click += (_, _) => StepOnce();

        stepSizeComboBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = this.AdjustForDPI(70) };
        stepSizeComboBox.Items.AddRange([.. StepSizes.Select(static size => size.Label)]);
        stepSizeComboBox.SelectedIndex = 0;

        var restartButton = new ThemedButton { Text = "Restart", AutoSize = true };
        restartButton.Click += (_, _) => RunLocked(() =>
        {
            session.Continue();
            viewer.ParticleNode?.Restart();
        });

        toolbar.Controls.Add(pauseButton);
        toolbar.Controls.Add(stepButton);
        toolbar.Controls.Add(stepSizeComboBox);
        toolbar.Controls.Add(restartButton);
        toolbar.Controls.Add(CreateHeading("Break on:"));

        foreach (var (anomaly, label, breakByDefault) in BreakOptions)
        {
            var checkBox = new CheckBox { Text = label, AutoSize = true, Checked = breakByDefault };

            if (breakByDefault)
            {
                session.BreakOn |= anomaly;
            }

            checkBox.CheckedChanged += (_, _) => session.BreakOn = checkBox.Checked ? session.BreakOn | anomaly : session.BreakOn & ~anomaly;
            toolbar.Controls.Add(checkBox);
        }

        toolbar.Controls.Add(CreateHeading("Overlay:"));
        toolbar.Controls.Add(CreateToggle("Particles", viewer.ShowDebugOverlay, value => viewer.ShowDebugOverlay = value));
        toolbar.Controls.Add(CreateToggle("Velocity", viewer.ShowDebugVelocity, value => viewer.ShowDebugVelocity = value));
        toolbar.Controls.Add(CreateToggle("Control points", viewer.ShowDebugControlPoints, value => viewer.ShowDebugControlPoints = value));

        statusLabel = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = this.AdjustForDPI(22),
            Padding = new Padding(6, 3, 0, 0),
        };

        // Systems
        systemImages = new ImageList { ImageSize = new Size(this.AdjustForDPI(10), this.AdjustForDPI(10)) };

        for (var i = 0; i < 6; i++)
        {
            var color = ParticleDebugRenderer.GetSystemColor(i);
            var bitmap = new Bitmap(systemImages.ImageSize.Width, systemImages.ImageSize.Height);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.FromArgb(color.A, color.R, color.G, color.B));
            }

            systemImages.Images.Add(bitmap);
        }

        systemTree = new TreeView
        {
            Dock = DockStyle.Fill,
            HideSelection = false,
            ImageList = systemImages,
            FullRowSelect = true,
        };
        systemTree.AfterSelect += (_, e) =>
        {
            if (!updating && e.Node?.Tag is ParticleSystemSimulation system)
            {
                SelectSystem(system);
            }
        };

        // Pipeline
        pipelineList = CreateList(("Stage", 110), ("Function", 220), ("Ran", 60), ("Particles", 70), ("Spawned / killed", 100), ("Changed", 200), ("Introduced", 180), ("Warnings", 60));
        pipelineList.CheckBoxes = true;
        pipelineList.ItemCheck += OnPipelineItemCheck;
        pipelineList.SelectedIndexChanged += (_, _) =>
        {
            if (!updating)
            {
                selectedRow = pipelineList.SelectedItems.Count > 0 ? (PipelineRow?)pipelineList.SelectedItems[0].Tag : null;
                RefreshNow();
            }
        };
        pipelineList.ContextMenuStrip = CreatePipelineMenu();

        // Particles at the selected step
        particleList = CreateList(("#", 40), ("Id", 50), ("Age", 60), ("Life", 60), ("Position", 190), ("Radius", 60), ("Alpha", 55), ("Color", 120), ("Anomalies", 160));
        particleList.VirtualMode = true;
        particleList.RetrieveVirtualItem += OnRetrieveParticle;
        particleList.SelectedIndexChanged += (_, _) =>
        {
            if (updating || particleList.SelectedIndices.Count == 0)
            {
                return;
            }

            var index = particleList.SelectedIndices[0];

            if (index < shownParticles.Length)
            {
                selectedParticleId = shownParticles[index].UniqueParticleId;
                viewer.DebugSelectedParticleId = selectedParticleId;
                RefreshNow();
            }
        };

        // One particle before and after the selected step
        inspectorLabel = new Label { Dock = DockStyle.Top, AutoSize = false, Height = this.AdjustForDPI(20), Padding = new Padding(4, 3, 0, 0) };
        inspectorList = CreateList(("Attribute", 140), ("Before", 140), ("After", 140));

        var inspectorPanel = new Panel { Dock = DockStyle.Fill };
        inspectorPanel.Controls.Add(inspectorList);
        inspectorPanel.Controls.Add(inspectorLabel);

        var particleSplit = CreateSplit(Orientation.Vertical, particleList, inspectorPanel, 540);
        var pipelineSplit = CreateSplit(Orientation.Horizontal, pipelineList, particleSplit, 330);

        // Other tabs
        problemList = CreateList(("Severity", 70), ("System", 200), ("Problem", 800));
        problemList.DoubleClick += (_, _) =>
        {
            if (problemList.SelectedItems.Count > 0 && problemList.SelectedItems[0].Tag is ParticleDiagnostic diagnostic)
            {
                ShowFunction(diagnostic.System, diagnostic.Function);
            }
        };

        controlPointList = CreateList(("CP", 40), ("Position", 220), ("Forward", 220), ("Changed by", 400));

        monospaceFont = new Font(FontFamily.GenericMonospace, 9f);
        functionText = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = monospaceFont,
        };

        var tabs = new ThemedTabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(CreateTab("Pipeline", pipelineSplit));
        tabs.TabPages.Add(CreateTab("Problems", problemList));
        tabs.TabPages.Add(CreateTab("Control Points", controlPointList));
        tabs.TabPages.Add(CreateTab("Function", functionText));

        var mainSplit = CreateSplit(Orientation.Vertical, systemTree, tabs, 260);

        Controls.Add(mainSplit);
        Controls.Add(statusLabel);
        Controls.Add(toolbar);

        refreshTimer = new Timer { Interval = 250 };
        refreshTimer.Tick += (_, _) => Refresh(force: false);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        RefreshNow();
        refreshTimer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        refreshTimer.Stop();

        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            refreshTimer.Dispose();
            systemImages.Dispose();
            monospaceFont.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.F5:
                TogglePause();
                return true;

            case Keys.F10:
                StepOnce();
                return true;

            case Keys.Escape:
                Close();
                return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void TogglePause()
    {
        RunLocked(() =>
        {
            if (viewer.ParticleNode is not { } node)
            {
                return;
            }

            session.Continue();
            viewer.SetPaused(!node.IsPaused);
        });

        RefreshNow();
    }

    private void StepOnce()
    {
        RunLocked(() =>
        {
            if (viewer.ParticleNode is not { } node)
            {
                return;
            }

            session.Continue();
            viewer.SetPaused(true);
            node.Step(StepSizes[Math.Max(0, stepSizeComboBox.SelectedIndex)].Seconds);
        });

        // The step happens on the next frame
        refreshTimer.Stop();
        refreshTimer.Interval = 50;
        refreshTimer.Start();
    }

    private void RefreshNow() => Refresh(force: true);

    private void Refresh(bool force)
    {
        if (refreshTimer.Interval != 250)
        {
            refreshTimer.Interval = 250;
            force = true;
        }

        RunLocked(() =>
        {
            if (viewer.ParticleNode is not { } node)
            {
                return;
            }

            root ??= node.ParticleSimulation;

            if (systemTree.Nodes.Count == 0)
            {
                BuildTree();
            }

            var system = selectedSystem ?? root;
            var stepNumber = system.Trace?.StepNumber ?? -1;

            if (session.Break is { } hit && hit != shownBreak)
            {
                shownBreak = hit;
                ShowStep(hit.System, hit.Step);
                force = true;
            }

            UpdateStatus(node);
            UpdateTreeText();

            if (!force && shownState == (stepNumber, version))
            {
                return;
            }

            shownState = (stepNumber, version);

            if (selectedSystem == null)
            {
                SelectSystem(root, refresh: false);
            }

            UpdatePipeline();
            UpdateParticles();
            UpdateInspector();
            UpdateControlPoints();
            UpdateFunctionText();
            UpdateProblems();
        });
    }

    private void RunLocked(Action action)
    {
        if (!IsDisposed)
        {
            viewer.RunLocked(action);
        }
    }

    private void UpdateStatus(ValveResourceFormat.Renderer.SceneNodes.ParticleSceneNode node)
    {
        var text = new StringBuilder();

        if (session.Break is { } hit)
        {
            text.Append(CultureInfo.InvariantCulture, $"Stopped: {hit.Step} in {ShortName(hit.System)} introduced {ParticleDiagnostics.Describe(hit.Anomalies)}. F5 resumes, F10 steps.");
        }
        else
        {
            text.Append(node.IsPaused ? "Paused" : "Running");
        }

        if (selectedSystem?.Trace is { } trace)
        {
            text.Append(CultureInfo.InvariantCulture, $"   |   step {trace.StepNumber}, {trace.StepTime * 1000f:0.##} ms, system age {trace.SystemAge:0.###} s");
        }

        statusLabel.Text = text.ToString();
        statusLabel.ForeColor = session.Break != null ? errorFore : ForeColor;
        pauseButton.Text = node.IsPaused ? "Resume" : "Pause";
        viewer.RefreshPauseButton();
    }

    private void BuildTree()
    {
        Debug.Assert(root != null);

        updating = true;
        systemTree.BeginUpdate();

        var index = 0;
        systemTree.Nodes.Add(CreateTreeNode(root, ref index));
        systemTree.ExpandAll();

        systemTree.EndUpdate();
        updating = false;
    }

    private TreeNode CreateTreeNode(ParticleSystemSimulation system, ref int index)
    {
        var imageIndex = index++ % systemImages.Images.Count;
        var treeNode = new TreeNode { Tag = system, ImageIndex = imageIndex, SelectedImageIndex = imageIndex };

        foreach (var child in system.Children)
        {
            treeNode.Nodes.Add(CreateTreeNode(child, ref index));
        }

        return treeNode;
    }

    private void UpdateTreeText()
    {
        foreach (var treeNode in EnumerateTreeNodes(systemTree.Nodes))
        {
            var system = (ParticleSystemSimulation)treeNode.Tag!;
            var text = string.Create(CultureInfo.InvariantCulture, $"{ShortName(system)}   {system.Particles.Count}/{system.MaxParticles}");

            if (system.DormantReason != null)
            {
                text += "   (not running)";
            }
            else if (system.IsFrozen)
            {
                text += "   (frozen)";
            }

            if (treeNode.Text != text)
            {
                treeNode.Text = text;
            }

            treeNode.ForeColor = system.DormantReason != null ? mutedFore : systemTree.ForeColor;
        }
    }

    private static IEnumerable<TreeNode> EnumerateTreeNodes(TreeNodeCollection nodes)
    {
        foreach (TreeNode treeNode in nodes)
        {
            yield return treeNode;

            foreach (var child in EnumerateTreeNodes(treeNode.Nodes))
            {
                yield return child;
            }
        }
    }

    private void SelectSystem(ParticleSystemSimulation system, bool refresh = true)
    {
        selectedSystem = system;
        selectedRow = null;
        selectedParticleId = -1;
        viewer.DebugSelectedSystem = system;
        viewer.DebugSelectedParticleId = -1;

        updating = true;

        if (EnumerateTreeNodes(systemTree.Nodes).FirstOrDefault(treeNode => treeNode.Tag == system) is { } treeNode)
        {
            systemTree.SelectedNode = treeNode;
        }

        BuildPipelineRows(system);
        updating = false;

        if (refresh)
        {
            RefreshNow();
        }
    }

    private void BuildPipelineRows(ParticleSystemSimulation system)
    {
        rows.Clear();
        rows.Add(new PipelineRow(null, null, StepStartLabel));

        foreach (var stage in StageOrder)
        {
            if (stage == ParticleFunctionStage.Initializer)
            {
                rows.Add(new PipelineRow(stage, null, ConstantsLabel));
            }

            foreach (var function in system.DebugFunctions.Where(function => function.Stage == stage).OrderBy(static function => function.DefinitionIndex))
            {
                rows.Add(new PipelineRow(stage, function, function.ShortName));
            }

            if (stage == ParticleFunctionStage.Constraint)
            {
                rows.Add(new PipelineRow(null, null, RemoveExpiredLabel));
            }
        }

        pipelineList.BeginUpdate();
        pipelineList.Items.Clear();

        foreach (var row in rows)
        {
            row.Item.Tag = row;
            row.Item.Text = row.Stage?.ToString() ?? string.Empty;

            while (row.Item.SubItems.Count < pipelineList.Columns.Count)
            {
                row.Item.SubItems.Add(string.Empty);
            }

            row.Item.Checked = TakesPart(row);
            pipelineList.Items.Add(row.Item);
        }

        pipelineList.EndUpdate();
    }

    private List<ParticleTraceStep> StepsOf(PipelineRow row)
        => selectedSystem?.Trace is { } trace ? [.. trace.Steps.Where(row.Matches)] : [];

    private void UpdatePipeline()
    {
        updating = true;
        pipelineList.BeginUpdate();

        foreach (var row in rows)
        {
            var item = row.Item;
            var function = row.Function;
            var steps = StepsOf(row);

            item.Checked = TakesPart(row);
            item.SubItems[1].Text = function == null ? row.Label : DescribeFunctionName(function);
            item.SubItems[2].Text = DescribeRan(row, steps);
            item.SubItems[3].Text = steps.Count == 0 ? string.Empty
                : steps[0].CountBefore == steps[^1].Count ? steps[^1].Count.ToString(CultureInfo.InvariantCulture)
                : string.Create(CultureInfo.InvariantCulture, $"{steps[0].CountBefore} -> {steps[^1].Count}");

            var killed = steps.Sum(static step => step.Killed);
            var spawned = row.Stage == ParticleFunctionStage.Emitter && steps.Count > 0 ? Math.Max(0, steps[^1].Count - steps[0].CountBefore) : 0;
            item.SubItems[4].Text = (spawned, killed) switch
            {
                (0, 0) => string.Empty,
                (_, 0) => string.Create(CultureInfo.InvariantCulture, $"+{spawned}"),
                (0, _) => string.Create(CultureInfo.InvariantCulture, $"-{killed}"),
                _ => string.Create(CultureInfo.InvariantCulture, $"+{spawned} -{killed}"),
            };

            var changed = steps.Aggregate(0UL, static (mask, step) => mask | step.ChangedAttributes);
            var changedControlPoints = steps.SelectMany(static step => step.ChangedControlPoints).Distinct().Order().ToList();
            var changedText = ParticleAttribute.DescribeMask(changed);

            if (changedControlPoints.Count > 0)
            {
                changedText = string.Join("; ", new[] { changedText, $"CP {string.Join(", ", changedControlPoints)}" }.Where(static text => text.Length > 0));
            }

            item.SubItems[5].Text = changedText;

            var introduced = steps.Aggregate(ParticleAnomalies.None, static (anomalies, step) => anomalies | step.Introduced);
            item.SubItems[6].Text = introduced == ParticleAnomalies.None ? string.Empty
                : string.Create(CultureInfo.InvariantCulture, $"{ParticleDiagnostics.Describe(introduced)} ({steps.Sum(static step => step.IntroducedCount)})");

            item.SubItems[7].Text = function is { Warnings.Count: > 0 } ? function.Warnings.Count.ToString(CultureInfo.InvariantCulture) : string.Empty;

            item.BackColor = (introduced & ParticleAnomalyDetector.Severe) != 0 ? severeBack
                : introduced != ParticleAnomalies.None ? invisibleBack
                : pipelineList.BackColor;

            item.ForeColor = function switch
            {
                { Status: ParticleFunctionStatus.Unsupported } => errorFore,
                { Status: ParticleFunctionStatus.Disabled } or { Bypassed: true } => mutedFore,
                _ when steps.Count > 0 && steps.TrueForAll(static step => step.Skipped) => mutedFore,
                _ => pipelineList.ForeColor,
            };
        }

        if (selectedRow != null && !selectedRow.Item.Selected)
        {
            selectedRow.Item.Selected = true;
            selectedRow.Item.EnsureVisible();
        }

        pipelineList.EndUpdate();
        updating = false;
    }

    /// <summary>Whether the row's checkbox shows it as part of the pipeline: bookkeeping always is.</summary>
    private static bool TakesPart(PipelineRow row) => row.Function is not { } function || (function.CanBypass && !function.Bypassed);

    private static string DescribeFunctionName(ParticleDebugFunction function) => function switch
    {
        { Status: ParticleFunctionStatus.Disabled } => $"{function.ShortName} (disabled)",
        { Status: ParticleFunctionStatus.Unsupported } => $"{function.ShortName} (not implemented)",
        { Bypassed: true } => $"{function.ShortName} (bypassed)",
        _ => function.ShortName,
    };

    private static string DescribeRan(PipelineRow row, List<ParticleTraceStep> steps)
    {
        if (row.Stage == ParticleFunctionStage.ForceGenerator && row.Function?.Status == ParticleFunctionStatus.Active)
        {
            return "in movement";
        }

        if (steps.Count == 0)
        {
            return string.Empty;
        }

        if (row.Label == ConstantsLabel)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{steps[0].Initialized} new");
        }

        if (row.Stage == ParticleFunctionStage.Initializer)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{steps[0].Initialized} new");
        }

        if (steps.TrueForAll(static step => step.Skipped))
        {
            return "skipped";
        }

        var strength = steps.Max(static step => step.Strength);
        var text = strength.ToString("0.##", CultureInfo.InvariantCulture);

        return steps.Count > 1 ? string.Create(CultureInfo.InvariantCulture, $"{text} x{steps.Count}") : text;
    }

    /// <summary>The step whose particles the selected row shows, and the one before it.</summary>
    private (ParticleTraceStep? Before, ParticleTraceStep? After) SelectedSteps()
    {
        if (selectedSystem?.Trace is not { Steps.Count: > 0 } trace)
        {
            return (null, null);
        }

        var steps = trace.Steps;
        var matched = selectedRow != null ? StepsOf(selectedRow) : [];

        // Rows that did not run in the step show where it ended
        if (matched.Count == 0)
        {
            return (null, steps[^1]);
        }

        for (var i = 1; i < steps.Count; i++)
        {
            if (steps[i] == matched[0])
            {
                return (steps[i - 1], matched[^1]);
            }
        }

        return (null, matched[^1]);
    }

    private void UpdateParticles()
    {
        if (selectedSystem == null)
        {
            return;
        }

        var (_, after) = SelectedSteps();
        shownParticles = after != null ? after.Particles.ToArray() : selectedSystem.Particles.Current.ToArray();
        shownOrigin = selectedSystem.MainControlPoint.Position;

        updating = true;
        particleList.BeginUpdate();
        particleList.VirtualListSize = shownParticles.Length;
        particleList.SelectedIndices.Clear();

        var selectedIndex = Array.FindIndex(shownParticles, particle => particle.UniqueParticleId == selectedParticleId);

        if (selectedIndex >= 0)
        {
            particleList.SelectedIndices.Add(selectedIndex);
        }

        particleList.EndUpdate();
        particleList.Invalidate();
        updating = false;
    }

    private void OnRetrieveParticle(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex >= shownParticles.Length)
        {
            e.Item = new ListViewItem(new string[particleList.Columns.Count]);
            return;
        }

        ref var particle = ref shownParticles[e.ItemIndex];
        var anomalies = ParticleAnomalyDetector.Detect(in particle, shownOrigin);

        e.Item = new ListViewItem(
        [
            e.ItemIndex.ToString(CultureInfo.InvariantCulture),
            particle.UniqueParticleId.ToString(CultureInfo.InvariantCulture),
            ParticleAttribute.FormatFloat(particle.Age),
            ParticleAttribute.FormatFloat(particle.Lifetime),
            FormatVector(particle.Position),
            ParticleAttribute.FormatFloat(particle.Radius),
            ParticleAttribute.FormatFloat(particle.Alpha),
            FormatVector(particle.Color),
            particle.MarkedAsKilled ? $"killed {ParticleDiagnostics.Describe(anomalies)}".TrimEnd() : ParticleDiagnostics.Describe(anomalies),
        ])
        {
            BackColor = (anomalies & ParticleAnomalyDetector.Severe) != 0 ? severeBack
                : (anomalies & InvisibleAnomalies) != 0 ? invisibleBack
                : particleList.BackColor,
            ForeColor = particle.MarkedAsKilled ? mutedFore : particleList.ForeColor,
        };
    }

    private void UpdateInspector()
    {
        inspectorList.BeginUpdate();
        inspectorList.Items.Clear();

        var (before, after) = FindInspectedStates(out var heading);
        inspectorLabel.Text = heading;

        if (after != null || before != null)
        {
            foreach (var attribute in ParticleAttribute.All)
            {
                var beforeText = before is { } b ? attribute.Format(in b) : string.Empty;
                var afterText = after is { } a ? attribute.Format(in a) : string.Empty;

                var item = new ListViewItem([attribute.Name, beforeText, afterText]);

                if (before is { } bb && after is { } aa && !attribute.ValuesEqual(in bb, in aa))
                {
                    item.BackColor = changedBack;
                }

                if ((after is { } af && !attribute.IsFinite(in af)) || (before is { } bf && !attribute.IsFinite(in bf)))
                {
                    item.ForeColor = errorFore;
                }

                inspectorList.Items.Add(item);
            }
        }

        inspectorList.EndUpdate();
    }

    private (Particle? Before, Particle? After) FindInspectedStates(out string heading)
    {
        if (selectedParticleId < 0 || selectedSystem == null)
        {
            heading = "Select a particle to inspect";
            return (null, null);
        }

        var name = selectedRow?.Function?.ShortName ?? selectedRow?.Label ?? "the step";

        // Initializers ran when the particle spawned, which its spawn trace kept
        if (selectedRow?.Stage == ParticleFunctionStage.Initializer)
        {
            if (selectedSystem.Trace is not { } trace || !trace.TryGetSpawnTrace(selectedParticleId, out var spawn))
            {
                heading = string.Create(CultureInfo.InvariantCulture, $"Particle {selectedParticleId} spawned before tracing started");
                return (null, null);
            }

            var stages = spawn.Stages;
            var stageIndex = selectedRow.Function == null ? 0 : FindStage(stages, selectedRow.Function);

            if (stageIndex < 0)
            {
                heading = $"{name} is not part of the spawn";
                return (null, null);
            }

            var outcome = stageIndex == 0 ? "the state before any initializer" : DescribeOutcome(stages[stageIndex].Outcome);
            heading = string.Create(CultureInfo.InvariantCulture, $"Particle {selectedParticleId} spawned at {spawn.SystemAge:0.###} s, {name}: {outcome}");

            return (stageIndex > 0 ? stages[stageIndex - 1].State : null, stages[stageIndex].State);
        }

        var (beforeStep, afterStep) = SelectedSteps();
        var after = FindParticle(afterStep, selectedParticleId);
        var before = FindParticle(beforeStep, selectedParticleId);

        heading = after == null
            ? string.Create(CultureInfo.InvariantCulture, $"Particle {selectedParticleId} is not alive after {name}")
            : before == null && beforeStep != null
                ? string.Create(CultureInfo.InvariantCulture, $"Particle {selectedParticleId} was spawned by {name}")
                : string.Create(CultureInfo.InvariantCulture, $"Particle {selectedParticleId} before and after {name}");

        return (before, after);
    }

    private static int FindStage(IReadOnlyList<ParticleSpawnStage> stages, ParticleDebugFunction function)
    {
        for (var i = 1; i < stages.Count; i++)
        {
            if (stages[i].Initializer == function)
            {
                return i;
            }
        }

        return -1;
    }

    private static string DescribeOutcome(ParticleSpawnOutcome outcome) => outcome switch
    {
        ParticleSpawnOutcome.Ran => "ran",
        ParticleSpawnOutcome.NotInPhase => "skipped, not in its endcap phase",
        ParticleSpawnOutcome.AlreadyWritten => "skipped, earlier initializers already wrote everything it writes",
        ParticleSpawnOutcome.Bypassed => "bypassed",
        _ => outcome.ToString(),
    };

    private static Particle? FindParticle(ParticleTraceStep? step, int uniqueParticleId)
    {
        if (step == null)
        {
            return null;
        }

        foreach (ref readonly var particle in step.Particles)
        {
            if (particle.UniqueParticleId == uniqueParticleId)
            {
                return particle;
            }
        }

        return null;
    }

    private void UpdateControlPoints()
    {
        if (selectedSystem == null)
        {
            return;
        }

        controlPointList.BeginUpdate();
        controlPointList.Items.Clear();

        var steps = selectedSystem.Trace?.Steps ?? [];
        IEnumerable<ParticleControlPointState> points;

        if (steps.Count > 0)
        {
            points = steps[^1].ControlPoints;
        }
        else
        {
            var live = new SortedList<int, ControlPoint>();
            selectedSystem.RenderState.CollectControlPoints(live);
            points = live.Select(static pair => new ParticleControlPointState(pair.Key, pair.Value.Position, pair.Value.Orientation));
        }

        foreach (var point in points)
        {
            var changedBy = string.Join(", ", steps.Where(step => step.ChangedControlPoints.Contains(point.Index)).Select(static step => step.ToString()));
            var item = new ListViewItem([point.Index.ToString(CultureInfo.InvariantCulture), FormatVector(point.Position), FormatVector(point.Orientation), changedBy]);

            if (!float.IsFinite(point.Position.LengthSquared()) || !float.IsFinite(point.Orientation.LengthSquared()))
            {
                item.ForeColor = errorFore;
            }

            controlPointList.Items.Add(item);
        }

        controlPointList.EndUpdate();
    }

    private void UpdateFunctionText()
    {
        if (selectedRow?.Function is not { } function)
        {
            functionText.Text = selectedRow == null
                ? "Select a function in the pipeline to see its definition."
                : $"{selectedRow.Label} is part of the simulation's own bookkeeping.";
            return;
        }

        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"{function.ClassName}");
        text.AppendLine(CultureInfo.InvariantCulture, $"{function.Stage} #{function.DefinitionIndex}, {function.Status}{(function.Bypassed ? ", bypassed" : string.Empty)}");

        if (function.Note != null)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Note: {function.Note}");
        }

        if (function.Warnings.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Warnings:");

            foreach (var warning in function.Warnings)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  {warning}");
            }
        }

        text.AppendLine();
        text.Append(function.Definition.ToKV3String());

        var newText = text.ToString().ReplaceLineEndings("\r\n");

        if (functionText.Text != newText)
        {
            functionText.Text = newText;
        }
    }

    private void UpdateProblems()
    {
        Debug.Assert(root != null);

        var diagnostics = ParticleDiagnostics.Collect(root);

        problemList.BeginUpdate();
        problemList.Items.Clear();

        foreach (var diagnostic in diagnostics)
        {
            var item = new ListViewItem([diagnostic.Severity.ToString(), ShortName(diagnostic.System), diagnostic.Message])
            {
                Tag = diagnostic,
                ForeColor = diagnostic.Severity switch
                {
                    ParticleDiagnosticSeverity.Error => errorFore,
                    ParticleDiagnosticSeverity.Info => mutedFore,
                    _ => problemList.ForeColor,
                },
            };

            problemList.Items.Add(item);
        }

        problemList.EndUpdate();
    }

    private void OnPipelineItemCheck(object? sender, ItemCheckEventArgs e)
    {
        if (updating)
        {
            return;
        }

        var row = (PipelineRow)pipelineList.Items[e.Index].Tag!;

        if (row.Function is not { CanBypass: true } function)
        {
            e.NewValue = e.CurrentValue;
            return;
        }

        var bypassed = e.NewValue == CheckState.Unchecked;
        RunLocked(() => function.Bypassed = bypassed);

        version++;
        BeginInvoke(RefreshNow);
    }

    private ThemedContextMenuStrip CreatePipelineMenu()
    {
        var menu = new ThemedContextMenuStrip();

        menu.Items.Add("Bypass everything after this", null, (_, _) =>
        {
            if (selectedRow == null)
            {
                return;
            }

            var after = rows.SkipWhile(row => row != selectedRow).Skip(1);
            SetBypassed(after.Where(static row => row.Stage != ParticleFunctionStage.Renderer), true);
        });

        menu.Items.Add("Bypass only this", null, (_, _) =>
        {
            if (selectedRow != null)
            {
                SetBypassed(rows, false);
                SetBypassed([selectedRow], true);
            }
        });

        menu.Items.Add("Enable everything", null, (_, _) => SetBypassed(rows, false));

        return menu;
    }

    private void SetBypassed(IEnumerable<PipelineRow> targets, bool bypassed)
    {
        RunLocked(() =>
        {
            foreach (var row in targets)
            {
                row.Function?.Bypassed = bypassed;
            }
        });

        version++;
        RefreshNow();
    }

    /// <summary>Selects <paramref name="system"/> and the row of the function a step belongs to.</summary>
    private void ShowStep(ParticleSystemSimulation system, ParticleTraceStep step)
    {
        if (selectedSystem != system)
        {
            SelectSystem(system, refresh: false);
        }

        selectedRow = rows.FirstOrDefault(row => row.Matches(step));
    }

    private void ShowFunction(ParticleSystemSimulation system, ParticleDebugFunction? function)
    {
        if (selectedSystem != system)
        {
            SelectSystem(system, refresh: false);
        }

        if (function != null)
        {
            selectedRow = rows.FirstOrDefault(row => row.Function == function);
        }

        RefreshNow();
    }

    private static string ShortName(ParticleSystemSimulation system) => System.IO.Path.GetFileNameWithoutExtension(system.Name);

    private static string FormatVector(Vector3 vector)
        => string.Create(CultureInfo.InvariantCulture, $"{ParticleAttribute.FormatFloat(vector.X)}, {ParticleAttribute.FormatFloat(vector.Y)}, {ParticleAttribute.FormatFloat(vector.Z)}");

    private ListView CreateList(params (string Name, int Width)[] columns)
    {
        var listView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false,
            GridLines = false,
        };

        foreach (var (name, width) in columns)
        {
            listView.Columns.Add(name, this.AdjustForDPI(width));
        }

        return listView;
    }

    private static Label CreateHeading(string text) => new() { Text = text, AutoSize = true, Padding = new Padding(12, 6, 0, 0) };

    private static CheckBox CreateToggle(string text, bool value, Action<bool> changed)
    {
        var checkBox = new CheckBox { Text = text, AutoSize = true, Checked = value };
        checkBox.CheckedChanged += (_, _) => changed(checkBox.Checked);
        return checkBox;
    }

    private SplitContainer CreateSplit(Orientation orientation, Control first, Control second, int distance)
    {
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = orientation };
        split.Panel1.Controls.Add(first);
        split.Panel2.Controls.Add(second);

        // The distance can only be set once the container has its size
        split.HandleCreated += (_, _) => split.SplitterDistance = Math.Min(this.AdjustForDPI(distance), Math.Max(0, (orientation == Orientation.Vertical ? split.Width : split.Height) - split.SplitterWidth - 50));

        return split;
    }

    private static ThemedTabPage CreateTab(string text, Control content)
    {
        var page = new ThemedTabPage(text);
        page.Controls.Add(content);
        return page;
    }
}
