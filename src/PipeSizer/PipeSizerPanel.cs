using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using PipeSizer.Sizing;

// WPF control names collide with Revit API names (Revit.UI.TextBox, Revit.UI.ComboBox,
// Revit.DB.Control): alias the WPF ones.
using Control = System.Windows.Controls.Control;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using WpfGrid = System.Windows.Controls.Grid;
using WpfBinding = System.Windows.Data.Binding;

namespace PipeSizer;

/// <summary>
/// The dockable panel. Built entirely in code (no XAML) so it compiles anywhere.
/// The panel never touches the Revit API directly: user actions call App.RequestCheck /
/// App.RequestSelect, and results arrive via UpdateResults on the UI thread.
/// </summary>
public sealed class PipeSizerPanel : UserControl, IDockablePaneProvider
{
    private readonly PipeSizerSettings _settings = new();
    private readonly ObservableCollection<PipeRow> _rows = new();
    private readonly ObservableCollection<SystemInfo> _systems = new();

    private ComboBox _hydronicMaterial = null!;
    private ComboBox _domesticMaterial = null!;
    private TextBox _hydronicVmax = null!;
    private TextBox _hydronicHmax = null!;
    private TextBox _domesticVmax = null!;
    private TextBox _domesticHmax = null!;
    private TextBox _gasLength = null!;
    private ComboBox _gasFuel = null!;
    private ComboBox _gasPressure = null!;
    private ComboBox _gasMaterial = null!;
    private CheckBox _showAll = null!;
    private CheckBox _pauseLive = null!;
    private ListBox _systemsList = null!;
    private ComboBox _systemCategory = null!;
    private TextBlock _overrideLabel = null!;
    private TextBox _overrideBox = null!;
    private TextBlock _overrideUnit = null!;
    private Button _overrideApply = null!;
    private Button _overrideClear = null!;
    private PipeRow? _selectedRow;
    private DataGrid _grid = null!;
    private TextBlock _status = null!;
    private TextBlock _updateNotice = null!;

    private bool _updatingGrid;
    private bool _updatingCategoryCombo;
    private bool _updatingSystemsList;
    private string? _selectedSystemName;

    public PipeSizerSettings Settings => _settings;

    public PipeSizerPanel()
    {
        MinWidth = 340;
        // Explicit light theme: Revit's dark-themed dockable-pane host gives a
        // dark background, and WPF TextBlocks default to black text — black on
        // black made every label invisible (controls with their own white
        // backgrounds, like ComboBox/TextBox/DataGrid headers, still showed).
        // Forcing white background + black foreground makes all text readable
        // regardless of Revit's theme.
        Background = Brushes.White;
        Foreground = Brushes.Black;
        BuildUi();
        Loaded += (_, _) => App.RequestCheck();
    }

    public void SetupDockablePane(DockablePaneProviderData data)
    {
        data.FrameworkElement = this;
        data.InitialState = new DockablePaneState { DockPosition = DockPosition.Right };
    }

    /// <summary>Called by App (Revit thread) when a check finishes. Marshals to the UI thread.</summary>
    public void UpdateResults(CheckResult result)
    {
        Dispatcher.Invoke(() =>
        {
            _updatingGrid = true;
            _rows.Clear();
            foreach (var row in result.Rows)
            {
                if (row.Status is "Violation" or "Oversize" or "No size meets limits" or "Info")
                    _rows.Add(row);
                else if (_settings.ShowAll)
                    _rows.Add(row);
            }
            // Restore the selected row across the rebuild (by ElementId), so the
            // manual-flow group keeps tracking the same pipe after a re-check.
            // The pipe may have dropped out of the list (deleted, or now OK with
            // Show-all off) — then selection clears.
            if (_selectedRow is not null)
            {
                var restored = _rows.FirstOrDefault(r => r.ElementIdValue == _selectedRow.ElementIdValue);
                if (restored is not null)
                {
                    _selectedRow = restored;
                    _grid.SelectedItem = restored;
                }
                else
                {
                    _selectedRow = null;
                }
            }
            _updatingGrid = false;

            _updatingSystemsList = true;
            string? keepSelection = _selectedSystemName;
            _systems.Clear();
            foreach (var sys in result.Systems)
                _systems.Add(sys);
            _selectedSystemName = null;
            if (keepSelection is not null)
            {
                var match = _systems.FirstOrDefault(s =>
                    string.Equals(s.Name, keepSelection, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    _systemsList.SelectedItem = match;
                    _selectedSystemName = match.Name;
                }
            }
            _updatingSystemsList = false;
            RefreshCategoryCombo();
            RefreshOverrideGroup();
            _status.Text = result.Summary;
        });
    }

    public void ShowError(string message)
    {
        Dispatcher.Invoke(() => _status.Text = "Error: " + message);
    }

    /// <summary>Shows an update notice (e.g. "Update vX downloaded — restart Revit").</summary>
    public void ShowUpdateNotice(string text)
    {
        Dispatcher.Invoke(() =>
        {
            _updateNotice.Text = text;
            _updateNotice.Visibility = System.Windows.Visibility.Visible;
        });
    }

    // ----- UI construction -----

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(6), Background = Brushes.White };

        var header = new TextBlock
        {
            // Version read at runtime from the executing assembly — never hardcoded,
            // so the header always shows the build actually running.
            Text = $"PipeSizer — v{Updater.DisplayVersion(Assembly.GetExecutingAssembly().GetName().Version)} — live pipe sizing check",
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            Foreground = Brushes.Black,
            Margin = new Thickness(0, 0, 0, 6),
        };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        _status = new TextBlock { Text = "Press Refresh to check.", Foreground = Brushes.Black, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);

        _updateNotice = new TextBlock
        {
            Text = "",
            Visibility = System.Windows.Visibility.Collapsed,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.Black,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        DockPanel.SetDock(_updateNotice, Dock.Bottom);
        root.Children.Add(_updateNotice);

        // Body: settings + systems on top (scrollable, capped), results grid
        // filling the rest. The grid MUST NOT live inside the ScrollViewer:
        // unbounded height disables DataGrid virtualization and renders every
        // row at once — the main cause of the panel freezing on big models.
        var body = new WpfGrid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(body);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 360 };
        WpfGrid.SetRow(scroll, 0);
        body.Children.Add(scroll);
        var stack = new StackPanel();
        scroll.Content = stack;

        // --- Settings ---
        stack.Children.Add(SectionTitle("Settings"));

        stack.Children.Add(SectionTitle("Hydronic"));
        _hydronicMaterial = new ComboBox { ItemsSource = Materials.Names.ToList(), SelectedItem = _settings.HydronicMaterial, Margin = new Thickness(0, 0, 0, 4) };
        _hydronicMaterial.SelectionChanged += (_, _) =>
        {
            if (_hydronicMaterial.SelectedItem is string m) { _settings.HydronicMaterial = m; App.RequestCheck(); }
        };
        stack.Children.Add(Labeled("Hydronic material", _hydronicMaterial));

        _hydronicVmax = NumberBox(_settings.HydronicMaxVelocityFps, v => _settings.HydronicMaxVelocityFps = v);
        stack.Children.Add(Labeled("Hydronic max velocity (ft/s)", _hydronicVmax));
        _hydronicHmax = NumberBox(_settings.HydronicMaxFrictionPer100Ft, v => _settings.HydronicMaxFrictionPer100Ft = v);
        stack.Children.Add(Labeled("Hydronic max friction (ft/100 ft)", _hydronicHmax));

        stack.Children.Add(SectionTitle("Domestic"));
        _domesticMaterial = new ComboBox { ItemsSource = Materials.Names.ToList(), SelectedItem = _settings.DomesticMaterial, Margin = new Thickness(0, 0, 0, 4) };
        _domesticMaterial.SelectionChanged += (_, _) =>
        {
            if (_domesticMaterial.SelectedItem is string m) { _settings.DomesticMaterial = m; App.RequestCheck(); }
        };
        stack.Children.Add(Labeled("Domestic material", _domesticMaterial));

        _domesticVmax = NumberBox(_settings.DomesticMaxVelocityFps, v => _settings.DomesticMaxVelocityFps = v);
        stack.Children.Add(Labeled("Domestic max velocity (ft/s)", _domesticVmax));
        _domesticHmax = NumberBox(_settings.DomesticMaxFrictionPer100Ft, v => _settings.DomesticMaxFrictionPer100Ft = v);
        stack.Children.Add(Labeled("Domestic max friction (ft/100 ft)", _domesticHmax));

        stack.Children.Add(SectionTitle("Gas"));
        _gasLength = NumberBox(_settings.GasDesignLengthFt, v => _settings.GasDesignLengthFt = v);
        stack.Children.Add(Labeled("Gas design length (ft)", _gasLength));

        _gasFuel = new ComboBox
        {
            ItemsSource = new[] { "Natural gas", "Propane" },
            SelectedItem = _settings.GasFuel == GasFuel.Propane ? "Propane" : "Natural gas",
            Margin = new Thickness(0, 0, 0, 4),
        };
        _gasFuel.SelectionChanged += (_, _) =>
        {
            if (_gasFuel.SelectedItem is string s)
            {
                _settings.GasFuel = s == "Propane" ? GasFuel.Propane : GasFuel.NaturalGas;
                App.RequestCheck();
            }
        };
        stack.Children.Add(Labeled("Gas fuel", _gasFuel));

        _gasPressure = new ComboBox
        {
            ItemsSource = new[] { "Low (< 2 psi)", "2 psi" },
            SelectedItem = _settings.GasPressure == GasPressure.TwoPsi ? "2 psi" : "Low (< 2 psi)",
            Margin = new Thickness(0, 0, 0, 4),
        };
        _gasPressure.SelectionChanged += (_, _) =>
        {
            if (_gasPressure.SelectedItem is string s)
            {
                _settings.GasPressure = s == "2 psi" ? GasPressure.TwoPsi : GasPressure.Low;
                App.RequestCheck();
            }
        };
        stack.Children.Add(Labeled("Gas pressure", _gasPressure));

        _gasMaterial = new ComboBox
        {
            ItemsSource = new[] { "Metallic (Sch 40)", "CSST", "PE pipe" },
            SelectedItem = _settings.GasMaterial switch
            {
                GasMaterial.Csst => "CSST",
                GasMaterial.Pe => "PE pipe",
                _ => "Metallic (Sch 40)",
            },
            Margin = new Thickness(0, 0, 0, 4),
        };
        _gasMaterial.SelectionChanged += (_, _) =>
        {
            if (_gasMaterial.SelectedItem is string s)
            {
                _settings.GasMaterial = s switch
                {
                    "CSST" => GasMaterial.Csst,
                    "PE pipe" => GasMaterial.Pe,
                    _ => GasMaterial.Metallic,
                };
                App.RequestCheck();
            }
        };
        stack.Children.Add(Labeled("Gas material", _gasMaterial));

        _showAll = new CheckBox { Content = "Show all pipes (incl. OK rows)", IsChecked = _settings.ShowAll, Margin = new Thickness(0, 2, 0, 6) };
        _showAll.Checked += (_, _) => { _settings.ShowAll = true; App.RequestCheck(); };
        _showAll.Unchecked += (_, _) => { _settings.ShowAll = false; App.RequestCheck(); };
        stack.Children.Add(_showAll);

        _pauseLive = new CheckBox { Content = "Pause live checking", IsChecked = false, Margin = new Thickness(0, 2, 0, 6) };
        _pauseLive.Checked += (_, _) => App.SetPaused(true);
        _pauseLive.Unchecked += (_, _) => { App.SetPaused(false); App.RequestCheck(); };
        stack.Children.Add(_pauseLive);

        var refresh = new Button { Content = "Refresh", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 8), HorizontalAlignment = HorizontalAlignment.Left };
        refresh.Click += (_, _) => App.RequestCheck();
        var updateBtn = new Button { Content = "Check for updates", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
        updateBtn.Click += (_, _) => App.CheckForUpdatesOnDemand();
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal };
        btnRow.Children.Add(refresh);
        btnRow.Children.Add(updateBtn);
        stack.Children.Add(btnRow);

        // --- Systems ---
        stack.Children.Add(SectionTitle("Systems — click one to set its category"));
        _systemsList = new ListBox { DisplayMemberPath = "Display", ItemsSource = _systems, MaxHeight = 110, Margin = new Thickness(0, 0, 0, 4) };
        _systemsList.SelectionChanged += OnSystemSelected;
        stack.Children.Add(_systemsList);

        _systemCategory = new ComboBox { ItemsSource = Enum.GetNames(typeof(PipeCategory)), Margin = new Thickness(0, 0, 0, 8), IsEnabled = false };
        _systemCategory.SelectionChanged += OnCategoryChanged;
        stack.Children.Add(Labeled("Category for selected system", _systemCategory));

        // --- Manual flow override ---
        // Separate control group, NOT an editable Flow column: single-click on a
        // row selects, double-click zooms, so in-grid editing would conflict.
        // The user types a flow for the selected pipe instead of relying on
        // Revit's computed flow. Session-only, never written to the model.
        stack.Children.Add(SectionTitle("Manual flow override"));
        _overrideLabel = new TextBlock { Text = "Click a result row to select a pipe", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
        stack.Children.Add(_overrideLabel);
        _overrideBox = new TextBox { Margin = new Thickness(0, 0, 4, 0), IsEnabled = false, Width = 90 };
        _overrideBox.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                ApplyOverride();
                e.Handled = true;
            }
        };
        _overrideUnit = new TextBlock { Text = "GPM", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        _overrideApply = new Button { Content = "Apply", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 4, 0), IsEnabled = false };
        _overrideApply.Click += (_, _) => ApplyOverride();
        _overrideClear = new Button { Content = "Clear", Padding = new Thickness(12, 4, 12, 4), IsEnabled = false };
        _overrideClear.Click += (_, _) => ClearOverride();
        var overrideRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        overrideRow.Children.Add(_overrideBox);
        overrideRow.Children.Add(_overrideUnit);
        overrideRow.Children.Add(_overrideApply);
        overrideRow.Children.Add(_overrideClear);
        stack.Children.Add(overrideRow);

        // --- Results (own row: bounded height, so the grid can virtualize) ---
        var resultsPanel = new DockPanel();
        WpfGrid.SetRow(resultsPanel, 1);
        body.Children.Add(resultsPanel);
        var resultsTitle = SectionTitle("Results — click a row to select, double-click to zoom");
        DockPanel.SetDock(resultsTitle, Dock.Top);
        resultsPanel.Children.Add(resultsTitle);
        _grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            SelectionMode = DataGridSelectionMode.Single,
            ItemsSource = _rows,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            EnableRowVirtualization = true,
            EnableColumnVirtualization = true,
            MinHeight = 120,
        };
        VirtualizingPanel.SetIsVirtualizing(_grid, true);
        VirtualizingPanel.SetVirtualizationMode(_grid, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(_grid, true);
        _grid.Columns.Add(new DataGridTextColumn { Header = "ID", Binding = new WpfBinding(nameof(PipeRow.ElementIdValue)), Width = 80 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "System", Binding = new WpfBinding(nameof(PipeRow.System)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Category", Binding = new WpfBinding(nameof(PipeRow.Category)), Width = 85 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Flow", Binding = new WpfBinding(nameof(PipeRow.Flow)), Width = 110 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Current", Binding = new WpfBinding(nameof(PipeRow.CurrentSize)), Width = 80 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Required", Binding = new WpfBinding(nameof(PipeRow.RequiredSize)), Width = 90 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Check", Binding = new WpfBinding(nameof(PipeRow.Check)), Width = 130 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Actual", Binding = new WpfBinding(nameof(PipeRow.Actual)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Limit", Binding = new WpfBinding(nameof(PipeRow.Limit)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new WpfBinding(nameof(PipeRow.Status)), Width = 90 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Note", Binding = new WpfBinding(nameof(PipeRow.Note)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        _grid.SelectionChanged += OnRowSelected;
        _grid.MouseDoubleClick += (_, _) =>
        {
            if (_grid.SelectedItem is PipeRow row)
                App.RequestSelect(row.ElementId, zoom: true);
        };
        resultsPanel.Children.Add(_grid);

        Content = root;
    }

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        Foreground = Brushes.Black,
        Margin = new Thickness(0, 6, 0, 4),
    };

    private static StackPanel Labeled(string label, Control control)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 0, 2) };
        p.Children.Add(new TextBlock { Text = label, Foreground = Brushes.Black });
        p.Children.Add(control);
        return p;
    }

    /// <summary>TextBox that parses a positive number on Enter / focus loss; reverts on bad input.</summary>
    private TextBox NumberBox(double initial, Action<double> apply)
    {
        double current = initial;
        var tb = new TextBox { Text = current.ToString("0.##"), Margin = new Thickness(0, 0, 0, 4) };
        void Commit()
        {
            if (double.TryParse(tb.Text, out double v) && v > 0)
            {
                current = v;
                apply(v);
                tb.Text = v.ToString("0.##");
                App.RequestCheck();
            }
            else
            {
                tb.Text = current.ToString("0.##");
                _status.Text = "Invalid number — value unchanged.";
            }
        }
        tb.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                Commit();
                e.Handled = true;
            }
        };
        tb.LostFocus += (_, _) => Commit();
        return tb;
    }

    // ----- Events -----

    private void OnSystemSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSystemsList)
            return;
        if (_systemsList.SelectedItem is SystemInfo sys)
        {
            _selectedSystemName = sys.Name;
            RefreshCategoryCombo();
        }
        else
        {
            _selectedSystemName = null;
            _systemCategory.IsEnabled = false;
        }
    }

    private void RefreshCategoryCombo()
    {
        _updatingCategoryCombo = true;
        try
        {
            if (_selectedSystemName is not null &&
                _systems.FirstOrDefault(s => string.Equals(s.Name, _selectedSystemName, StringComparison.OrdinalIgnoreCase)) is { } sys)
            {
                _systemCategory.SelectedItem = sys.EffectiveCategory.ToString();
                _systemCategory.IsEnabled = true;
            }
            else
            {
                _systemCategory.IsEnabled = false;
            }
        }
        finally
        {
            _updatingCategoryCombo = false;
        }
    }

    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingCategoryCombo)
            return;
        if (_selectedSystemName is null || _systemCategory.SelectedItem is not string name)
            return;
        if (Enum.TryParse<PipeCategory>(name, out var cat))
        {
            _settings.CategoryOverrides[_selectedSystemName] = cat;
            App.RequestCheck();
        }
    }

    private void OnRowSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingGrid)
            return;
        if (_grid.SelectedItem is PipeRow row)
        {
            _selectedRow = row;
            RefreshOverrideGroup();
            App.RequestSelect(row.ElementId, zoom: false);
        }
        else
        {
            _selectedRow = null;
            RefreshOverrideGroup();
        }
    }

    /// <summary>Syncs the manual-flow group with the selected row (label, unit, pre-filled value, enabled state).</summary>
    private void RefreshOverrideGroup()
    {
        if (_selectedRow is null)
        {
            _overrideLabel.Text = "Click a result row to select a pipe";
            _overrideBox.Text = "";
            _overrideBox.IsEnabled = false;
            _overrideUnit.Text = "GPM";
            _overrideApply.IsEnabled = false;
            _overrideClear.IsEnabled = false;
            return;
        }
        _overrideLabel.Text = $"Selected pipe: {_selectedRow.ElementIdValue} ({_selectedRow.System}, {_selectedRow.Category})";
        Enum.TryParse<PipeCategory>(_selectedRow.Category, out var cat);
        _overrideUnit.Text = FlowOverrides.UnitLabel(cat, _settings.GasFuel);
        _overrideBox.IsEnabled = true;
        _overrideApply.IsEnabled = true;
        _overrideClear.IsEnabled = true;
        // Pre-fill an existing override for this pipe (blank = Revit's flow).
        _overrideBox.Text = _settings.FlowOverrides.TryGetValue(_selectedRow.ElementIdValue, out double existing)
            ? existing.ToString("0.##")
            : "";
    }

    /// <summary>Validates and stores a manual flow for the selected pipe, then re-checks.</summary>
    private void ApplyOverride()
    {
        if (_selectedRow is null)
            return;
        Enum.TryParse<PipeCategory>(_selectedRow.Category, out var cat);
        if (cat == PipeCategory.Ignore)
        {
            _status.Text = "Pipe is in an Ignored system — manual flow not applied.";
            return;
        }
        if (!FlowOverrides.TryParseFlow(_overrideBox.Text, out double value))
        {
            _status.Text = "Invalid flow — enter a positive number.";
            return;
        }
        _settings.FlowOverrides[_selectedRow.ElementIdValue] = value;
        _status.Text = $"Manual flow {value:0.##} {FlowOverrides.UnitLabel(cat, _settings.GasFuel)} applied to pipe {_selectedRow.ElementIdValue}.";
        App.RequestCheck();
    }

    /// <summary>Removes the manual flow for the selected pipe (if any), then re-checks.</summary>
    private void ClearOverride()
    {
        if (_selectedRow is null)
            return;
        if (_settings.FlowOverrides.Remove(_selectedRow.ElementIdValue))
            _status.Text = $"Manual flow cleared for pipe {_selectedRow.ElementIdValue} — back to Revit's flow.";
        else
            _status.Text = "No manual flow set for this pipe.";
        _overrideBox.Text = "";
        App.RequestCheck();
    }
}
