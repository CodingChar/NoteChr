using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using TextBox = System.Windows.Controls.TextBox;
using ListBox = System.Windows.Controls.ListBox;
using FontFamily = System.Windows.Media.FontFamily;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using MessageBox = System.Windows.MessageBox;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace NoteChr;

public partial class MainWindow : Window
{
    private const double BaseFontSize = 14;
    private const double MinZoom = 0.5;
    private const double MaxZoom = 3.0;
    private const double ZoomStep = 0.1;
    private const int MaxSuggestions = 10;

    /// <summary>Techo de coincidencias revisadas por pestaña: evita que un archivo enorme
    /// haga la búsqueda de sugerencias lenta en cada pulsación.</summary>
    private const int MaxSuggestionScan = 5000;

    private static readonly FontFamily EditorFontFamily = new("Consolas");

    /// <summary>Los archivos nuevos se guardan en UTF-8 sin BOM.</summary>
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    private static readonly Encoding Utf8WithBom = new UTF8Encoding(true);

    /// <summary>Se usa solo para detectar si un archivo sin BOM es UTF-8 válido.</summary>
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);

    private readonly ObservableCollection<TabItemModel> _tabs = new();
    private int _tabCounter = 1;
    private double _zoomFactor = 1.0;
    private string _findText = "";
    private string _replaceText = "";
    /// <summary>Evita crear una pestaña vacía mientras la ventana se está cerrando.</summary>
    private bool _isClosing;
    /// <summary>Impide que el autocompletado se reactive sobre texto escrito por el código.</summary>
    private bool _suppressCompletion;
    /// <summary>
    /// Al cambiar de pestaña, WPF dispara SelectionChanged. Se ignoran esos eventos para
    /// no sobrescribir el estado con "Pestaña: X" al guardar o cerrar.
    /// </summary>
    private bool _suppressSelectionEvent;
    private string? _workspaceRoot;
    private FileSystemWatcher? _workspaceWatcher;
    private readonly DispatcherTimer _explorerRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private double _explorerWidth = 260;
    private Dictionary<string, ThemeDefinition> _themes = new(StringComparer.OrdinalIgnoreCase);
    private ThemeDefinition? _currentTheme;

    public MainWindow()
    {
        InitializeComponent();
        LoadThemes();
        _explorerRefreshTimer.Tick += (_, _) =>
        {
            _explorerRefreshTimer.Stop();
            if (!string.IsNullOrEmpty(_workspaceRoot) && ExplorerColumn.Width.Value > 0)
                LoadExplorer(_workspaceRoot, preserveSelection: true);
        };
        // PreviewKeyDown y no KeyDown: el TextBox marca Tab como gestionada (AcceptsTab)
        // y, si llega hasta él, el evento deja de propagarse y Ctrl+Tab se pierde.
        PreviewKeyDown += MainWindow_PreviewKeyDown;

        // Enter dentro del panel de buscar: buscar, o reemplazar con Mayús.
        FindTextBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) Replace(); else FindNext_Click(FindTextBox, e);
            e.Handled = true;
        };
    }

    /// <summary>
    /// El foco se da al abrirse la ventana. App.OnStartup añade las pestañas antes de
    /// Show(), cuando la ventana todavía no está visible y Focus() no surte efecto.
    /// </summary>
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        FocusEditor();
    }

    /// <summary>
    /// Abre los archivos indicados en pestañas. Se usa al arrancar con argumentos,
    /// por ejemplo al elegir "Abrir con NoteChr" o al hacer doble clic en un .txt.
    /// Si no hay archivos, se crea una pestaña vacía para tener dónde escribir.
    /// </summary>
    /// <summary>
    /// Abre los archivos indicados en pestañas. Se usa al arrancar con argumentos,
    /// por ejemplo al elegir "Abrir con NoteChr" o al hacer doble clic en un .txt.
    /// Si no hay archivos, se crea una pestaña vacía para tener dónde escribir.
    /// El foco en el editor se asigna en OnContentRendered, no aquí: en este punto la
    /// ventana todavía no se ha mostrado y Focus() no surte efecto.
    /// </summary>
    public void OpenFilesOnStartup(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                var file = ReadTextFile(path);
                AddTab(Path.GetFileName(path), CreateEditor(file.Text), path, file.Encoding, file.Label);
            }
            catch (Exception ex)
            {
                UpdateStatus($"No se pudo abrir {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        if (_tabs.Count == 0) NewFile();
    }

    /// <summary>Abre una carpeta recibida desde el menú contextual de Windows.</summary>
    public void OpenFolderOnStartup(string path)
    {
        if (Directory.Exists(path)) LoadExplorer(path);
    }

    /// <summary>
    /// Lee un archivo de texto conservando su codificación. Sin esto, un archivo UTF-16 o
    /// ANSI se abriría como UTF-8 y al guardarlo se corrompería: por eso se devuelve también
    /// la codificación detectada, que <see cref="SaveFile"/> reutiliza.
    /// </summary>
    private static TextFile ReadTextFile(string path)
    {
        var bytes = File.ReadAllBytes(path);

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new TextFile(Utf8NoBom.GetString(bytes, 3, bytes.Length - 3), Utf8WithBom, "UTF-8 c/BOM");

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return new TextFile(Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), Encoding.Unicode, "UTF-16 LE");

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return new TextFile(Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), Encoding.BigEndianUnicode, "UTF-16 BE");

        // Sin BOM. Si los bytes no son UTF-8 válido el archivo es ANSI heredado: usar
        // ISO-8859-1 evita los caracteres de reemplazo que destrozan el contenido al guardar.
        try
        {
            return new TextFile(StrictUtf8.GetString(bytes), Utf8NoBom, "UTF-8");
        }
        catch (DecoderFallbackException)
        {
            return new TextFile(Encoding.Latin1.GetString(bytes), Encoding.Latin1, "ISO-8859-1");
        }
    }

    #region Tab Management

    /// <summary>Pestaña abierta. El Tag enlaza el control con su modelo de datos.</summary>
    private TabItemModel? Current => (MainTabControl.SelectedItem as TabItem)?.Tag as TabItemModel;

    private void NewFile()
    {
        AddTab($"Sin título {_tabCounter++}", CreateEditor(), null, Utf8NoBom, "UTF-8");
        UpdateStatus("Nuevo archivo creado");
    }

    /// <summary>Crea el TabItem explícitamente: evita que el ContentControl muestre el modelo con ToString().</summary>
    private TabItemModel AddTab(string title, Border editor, string? path, Encoding encoding, string encodingLabel)
    {
        var model = new TabItemModel
        {
            Title = title,
            Content = editor,
            FilePath = path,
            Encoding = encoding,
            EncodingLabel = encodingLabel
        };

        // La referencia la crea CreateEditor: recuperarla buscando entre los hijos del Grid
        // podía devolver un control derepisa, desconectado del editor real.
        var reference = (EditorReference)editor.Tag;
        reference.Owner = model;

        var closeButton = new Button
        {
            Style = (Style)FindResource("CloseTabButton"),
            Tag = model
        };
        closeButton.Click += CloseTabButton_Click;

        var titleBlock = new TextBlock
        {
            Text = model.Header,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        };
        model.HeaderBlock = titleBlock;

        var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
        headerPanel.Children.Add(titleBlock);
        if (!string.IsNullOrEmpty(path))
        {
            headerPanel.Children.Add(new TextBlock
            {
                Text = SummarizeDirectory(path),
                ToolTip = path,
                FontSize = 10,
                Foreground = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 1, 4, 0)
            });
        }
        headerPanel.Children.Add(closeButton);

        var tab = new TabItem
        {
            Header = headerPanel,
            Content = editor,
            Tag = model
        };

        _tabs.Add(model);
        MainTabControl.Items.Add(tab);
        MainTabControl.SelectedItem = tab;
        if (_currentTheme != null) ApplyTheme(_currentTheme);
        FocusEditor(model);
        return model;
    }

    /// <summary>El teclado debe ir al editor de la pestaña activa, no a la ventana.</summary>
    private void FocusEditor(TabItemModel? model = null)
    {
        var target = model ?? Current;
        if (FindEditor(target) is { } editor) FocusIfNeeded(editor);
    }

    /// <summary>
    /// Devuelve el foco al editor solo si no lo tiene ya. Hacerlo mientras cambia el texto
    /// reposiciona el cursor: al abrir el popup desde TextChanged, el carácter recién
    /// escrito se quedaba antes de donde el usuario lo había puesto.
    /// el cursor: al abrir el popup desde TextChanged, el carácter recién escrito se quedaba
    /// antes de donde el usuario lo había puesto.
    /// </summary>
    private static void FocusIfNeeded(UIElement element)
    {
        if (!element.IsKeyboardFocusWithin) element.Focus();
    }

    private void OpenFile()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Archivos de texto (*.txt)|*.txt|Todos los archivos (*.*)|*.*",
            Title = "Abrir archivo"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var file = ReadTextFile(dialog.FileName);
            AddTab(Path.GetFileName(dialog.FileName), CreateEditor(file.Text), dialog.FileName, file.Encoding, file.Label);
            UpdateStatus($"Abierto: {dialog.FileName} ({file.Label})");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"No se pudo abrir el archivo:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SelectFolder() is { } path) LoadExplorer(path);
    }

    /// <summary>Selector de carpetas nativo de Windows, sin cargar Windows Forms.</summary>
    private string? SelectFolder()
    {
        var displayName = Marshal.AllocCoTaskMem(260 * 2);
        var info = new BrowseInfo
        {
            Owner = new WindowInteropHelper(this).Handle,
            Title = "Selecciona una carpeta para explorar",
            DisplayName = displayName,
            Flags = 0x0001 | 0x0040 | 0x0050 // FSDIRS | NEWDIALOGSTYLE | USENEWUI
        };

        try
        {
            var item = BrowseForFolder(ref info);
            if (item == IntPtr.Zero) return null;

            try
            {
                var path = new StringBuilder(32768);
                return GetPathFromIdList(item, path) ? path.ToString() : null;
            }
            finally { Marshal.FreeCoTaskMem(item); }
        }
        finally { Marshal.FreeCoTaskMem(displayName); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BrowseInfo
    {
        public IntPtr Owner, Root;
        public IntPtr DisplayName;
        public string Title;
        public uint Flags;
        public IntPtr Callback, Param;
        public int Image;
    }

    [DllImport("shell32.dll", EntryPoint = "SHBrowseForFolderW", CharSet = CharSet.Unicode)]
    private static extern IntPtr BrowseForFolder(ref BrowseInfo info);

    [DllImport("shell32.dll", EntryPoint = "SHGetPathFromIDListW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPathFromIdList(IntPtr item, StringBuilder path);

    private void RefreshExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_workspaceRoot)) LoadExplorer(_workspaceRoot);
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string id } && _themes.TryGetValue(id, out var theme))
            ApplyTheme(theme);
    }

    private void LoadThemes()
    {
        try
        {
            var file = Path.Combine(AppContext.BaseDirectory, "Themes", "themes.json");
            var themes = File.Exists(file)
                ? JsonSerializer.Deserialize<List<ThemeDefinition>>(File.ReadAllText(file),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new()
                : new List<ThemeDefinition>();
            _themes = themes.Where(IsValidTheme)
                .GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
            if (_themes.Count == 0) _themes["classic"] = new ThemeDefinition();
            PopulateThemeMenu();
            if (_themes.TryGetValue("classic", out var classic)) ApplyTheme(classic);
        }
        catch (Exception ex)
        {
            // La aplicación debe seguir siendo utilizable aunque el usuario haya
            // editado el JSON y haya dejado una entrada inválida.
            _themes.Clear();
            _themes["classic"] = new ThemeDefinition();
            PopulateThemeMenu();
            ApplyTheme(_themes["classic"]);
            UpdateStatus($"No se pudieron cargar los temas: {ex.Message}");
        }
    }

    private void PopulateThemeMenu()
    {
        ThemeMenu.Items.Clear();
        foreach (var theme in _themes.Values.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var item = new MenuItem
            {
                Header = theme.Name,
                Tag = theme.Id,
                IsCheckable = true,
                Padding = new Thickness(8, 2, 8, 2),
                MinHeight = 0
            };
            item.Click += Theme_Click;
            ThemeMenu.Items.Add(item);
        }
    }

    private void ApplyTheme(ThemeDefinition theme)
    {
        _currentTheme = theme;
        var resources = Application.Current.Resources;
        SetBrush(resources, "BackgroundBrush", theme.Background);
        SetBrush(resources, "SurfaceBrush", theme.Surface);
        SetBrush(resources, "BorderBrush", theme.Border);
        SetBrush(resources, "TextPrimaryBrush", theme.TextPrimary);
        SetBrush(resources, "TextSecondaryBrush", theme.TextSecondary);
        SetBrush(resources, "AccentBrush", theme.Accent);
        SetBrush(resources, "AccentHoverBrush", theme.AccentHover);
        SetBrush(resources, "AccentPressedBrush", theme.AccentPressed);
        SetBrush(resources, "CompletionSelectedBrush", theme.Selection);
        SetBrush(resources, "MenuHoverBrush", theme.MenuHover);
        SetBrush(resources, "MenuPressedBrush", theme.Selection);
        SetBrush(resources, "TabBackgroundBrush", theme.TabBackground);
        SetBrush(resources, "TabActiveBackgroundBrush", theme.TabActive);
        SetBrush(resources, "StatusBarBackgroundBrush", theme.StatusBar);
        SetBrush(resources, "CompletionBackgroundBrush", theme.Surface);
        SetBrush(resources, "CompletionHoverBrush", theme.MenuHover);
        SetBrush(resources, "CompletionBorderBrush", theme.Accent);

        foreach (var tab in _tabs)
        {
            if (tab.Content is not Border border || border.Tag is not EditorReference reference) continue;
            border.Background = Brush(theme.EditorBackground);
            border.BorderBrush = Brush(theme.Border);
            reference.TextBox.Background = Brushes.Transparent;
            reference.TextBox.Foreground = Brush(theme.EditorForeground);
            reference.TextBox.CaretBrush = Brush(theme.Caret);
            reference.TextBox.SelectionBrush = Brush(theme.Selection);
            reference.LineNumbers.Foreground = Brush(theme.LineNumber);
            reference.LineNumbersHost.Background = Brush(theme.EditorBackground);
            if (reference.CompletionPopup.Child is Border completionBorder)
            {
                completionBorder.Background = Brush(theme.Surface);
                completionBorder.BorderBrush = Brush(theme.Accent);
            }
        }

        // Aplicación explícita para que el tema no dependa únicamente de que WPF
        // invalide un DynamicResource dentro de una plantilla ya materializada.
        Background = Brush(theme.Background);
        MainMenu.Background = Brush(theme.Surface);
        MainMenu.BorderBrush = Brush(theme.Border);
        MainToolbar.Background = Brush(theme.Surface);
        ExplorerBorder.Background = Brush(theme.Surface);
        ExplorerBorder.BorderBrush = Brush(theme.Border);
        ExplorerTree.Foreground = Brush(theme.TextPrimary);
        MainTabControl.Background = Brush(theme.Background);
        MainStatusBar.Background = Brush(theme.StatusBar);
        MainStatusBar.BorderBrush = Brush(theme.Border);
        FindPanel.Background = Brush(theme.Surface);
        FindPanel.BorderBrush = Brush(theme.Border);

        foreach (var tab in MainTabControl.Items.OfType<TabItem>())
        {
            tab.Background = Brush(theme.TabBackground);
            tab.BorderBrush = Brush(theme.Border);
            tab.Foreground = Brush(theme.TextPrimary);
        }

        foreach (MenuItem item in ThemeMenu.Items)
            item.IsChecked = string.Equals(item.Tag as string, theme.Id, StringComparison.OrdinalIgnoreCase);

        UpdateStatus($"Tema: {theme.Name}");
    }

    private static void SetBrush(ResourceDictionary resources, string key, string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) resources[key] = Brush(value);
    }

    private static SolidColorBrush Brush(string value) =>
        new((Color)ColorConverter.ConvertFromString(value)!);

    private static bool IsValidTheme(ThemeDefinition theme)
    {
        if (string.IsNullOrWhiteSpace(theme.Id) || string.IsNullOrWhiteSpace(theme.Name)) return false;
        var colors = new[]
        {
            theme.Background, theme.Surface, theme.Border, theme.TextPrimary,
            theme.TextSecondary, theme.Accent, theme.AccentHover, theme.AccentPressed,
            theme.Selection, theme.MenuHover, theme.MenuPressed, theme.TabBackground,
            theme.TabActive, theme.StatusBar, theme.EditorBackground, theme.EditorForeground,
            theme.LineNumber, theme.Caret
        };
        return colors.All(value => !string.IsNullOrWhiteSpace(value) &&
            value.StartsWith('#') && (value.Length == 7 || value.Length == 9) &&
            value.Skip(1).All(Uri.IsHexDigit));
    }

    private void ToggleExplorer_Click(object sender, RoutedEventArgs e) => ToggleExplorer();

    private void ToggleExplorer()
    {
        if (ExplorerColumn.Width.Value > 0)
        {
            _explorerWidth = ExplorerColumn.ActualWidth > 0 ? ExplorerColumn.ActualWidth : _explorerWidth;
            ExplorerColumn.Width = new GridLength(0);
        }
        else
        {
            ExplorerColumn.Width = new GridLength(Math.Max(180, _explorerWidth));
        }
    }

    private void ExplorerSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (ExplorerColumn.ActualWidth >= 120) _explorerWidth = ExplorerColumn.ActualWidth;
    }

    private void LoadExplorer(string path, bool preserveSelection = false)
    {
        try
        {
            var root = new DirectoryInfo(path);
            if (!root.Exists) return;

            _workspaceRoot = root.FullName;
            ExplorerRootText.Text = root.FullName;
            var selectedPath = preserveSelection && ExplorerTree.SelectedItem is TreeViewItem selected
                ? selected.Tag as string : null;
            ExplorerTree.Items.Clear();
            ExplorerTree.Items.Add(CreateDirectoryNode(root, true));
            if (selectedPath != null) SelectExplorerPath(selectedPath);
            ConfigureWorkspaceWatcher(root.FullName);
            UpdateStatus($"Carpeta abierta: {root.FullName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"No se pudo abrir la carpeta:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ConfigureWorkspaceWatcher(string path)
    {
        if (string.Equals(_workspaceWatcher?.Path, path, StringComparison.OrdinalIgnoreCase)) return;

        _workspaceWatcher?.Dispose();
        _workspaceWatcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };
        _workspaceWatcher.Changed += WorkspaceChanged;
        _workspaceWatcher.Created += WorkspaceChanged;
        _workspaceWatcher.Deleted += WorkspaceChanged;
        _workspaceWatcher.Renamed += WorkspaceChanged;
        _workspaceWatcher.Error += (_, _) => Dispatcher.BeginInvoke(() => UpdateStatus("No se pudo vigilar la carpeta; pulsa ↻ para actualizar."));
    }

    private void WorkspaceChanged(object sender, FileSystemEventArgs e)
    {
        // FileSystemWatcher puede emitir varios eventos por una sola operación.
        Dispatcher.BeginInvoke(() =>
        {
            if (!_explorerRefreshTimer.IsEnabled) _explorerRefreshTimer.Start();
        });
    }

    private void SelectExplorerPath(string path)
    {
        if (ExplorerTree.Items.Count == 0) return;
        var parts = Path.GetRelativePath(_workspaceRoot!, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        SelectExplorerNode(ExplorerTree.Items[0] as TreeViewItem, parts, 0);
    }

    private static bool SelectExplorerNode(TreeViewItem? node, string[] parts, int index)
    {
        if (node == null) return false;
        if (index >= parts.Length) { node.IsSelected = true; return true; }

        foreach (TreeViewItem child in node.Items)
        {
            if (child.Tag is string childPath && string.Equals(Path.GetFileName(childPath), parts[index], StringComparison.OrdinalIgnoreCase))
            {
                node.IsExpanded = true;
                return SelectExplorerNode(child, parts, index + 1);
            }
        }
        return false;
    }

    private static TreeViewItem CreateDirectoryNode(DirectoryInfo directory, bool expand)
    {
        var node = new TreeViewItem { Header = $"📁 {directory.Name}", Tag = directory.FullName, IsExpanded = expand };

        try
        {
            foreach (var child in directory.EnumerateDirectories()
                         .Where(d => !d.Attributes.HasFlag(FileAttributes.Hidden) && !d.Attributes.HasFlag(FileAttributes.System))
                         .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                node.Items.Add(CreateDirectoryNode(child, false));

            foreach (var file in directory.EnumerateFiles()
                         .Where(f => !f.Attributes.HasFlag(FileAttributes.Hidden) && !f.Attributes.HasFlag(FileAttributes.System))
                         .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                node.Items.Add(new TreeViewItem { Header = $"📄 {file.Name}", Tag = file.FullName });
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        return node;
    }

    private void ExplorerTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (ExplorerTree.SelectedItem is TreeViewItem { Tag: string path } && File.Exists(path))
            StatusText.Text = path;
    }

    private void ExplorerTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ExplorerTree.SelectedItem is not TreeViewItem { Tag: string path } || !File.Exists(path)) return;
        OpenFileFromPath(path);
        e.Handled = true;
    }

    private void OpenFileFromPath(string path)
    {
        try
        {
            var existing = _tabs.FirstOrDefault(t => string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                SelectTab(existing);
                return;
            }

            var file = ReadTextFile(path);
            AddTab(Path.GetFileName(path), CreateEditor(file.Text), path, file.Encoding, file.Label);
            UpdateStatus($"Abierto: {path}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"No se pudo abrir el archivo:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SelectTab(TabItemModel model)
    {
        var tab = MainTabControl.Items.OfType<TabItem>().FirstOrDefault(t => ReferenceEquals(t.Tag, model));
        if (tab != null) MainTabControl.SelectedItem = tab;
    }

    private string SummarizeDirectory(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath) ?? string.Empty;
        if (!string.IsNullOrEmpty(_workspaceRoot))
        {
            var relative = Path.GetRelativePath(_workspaceRoot, directory);
            if (relative == ".") return "./";
            if (!relative.StartsWith("..", StringComparison.Ordinal)) return $"./{relative}";
        }

        var parent = Directory.GetParent(directory);
        return parent == null ? directory : $"…{Path.DirectorySeparatorChar}{Path.GetFileName(directory)}";
    }

    private void RemoveTab(TabItemModel model)
    {
        var control = MainTabControl.Items.OfType<TabItem>().FirstOrDefault(t => ReferenceEquals(t.Tag, model));
        if (control == null) return;

        _tabs.Remove(model);
        MainTabControl.Items.Remove(control);

        // Al cerrar todas las pestañas no se abre una nueva: la ventana puede estar cerrándose.
        if (_tabs.Count == 0)
        {
            if (!_isClosing) NewFile();
            return;
        }

        UpdateStatus($"Pestaña cerrada: {model.Title}");
    }

    private void SaveFile()
    {
        var currentTab = Current;
        if (currentTab == null) return;

        if (string.IsNullOrEmpty(currentTab.FilePath))
        {
            SaveAs();
            return;
        }

        try
        {
            var editor = FindEditor(currentTab);
            if (editor == null) return;
            // Se reutiliza la codificación con la que se abrió: si el archivo era UTF-16,
            // escribirlo como UTF-8 destrozaría el contenido de forma irreversible.
            File.WriteAllText(currentTab.FilePath, editor.Text, currentTab.Encoding);
            currentTab.IsModified = false;
            UpdateStatus($"Guardado: {currentTab.FilePath} ({currentTab.EncodingLabel})");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"No se pudo guardar el archivo:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveAs()
    {
        var currentTab = Current;
        if (currentTab == null) return;

        // Si el nombre no trae extensión se añade .txt: sin ella el diálogo proposalonga
        // el nombre y el archivo acaba guardado sin extensión.
        var suggestedName = Path.GetFileName(currentTab.Title);
        if (string.IsNullOrEmpty(Path.GetExtension(suggestedName))) suggestedName += ".txt";

        var dialog = new SaveFileDialog
        {
            Filter = "Archivos de texto (*.txt)|*.txt|Todos los archivos (*.*)|*.*",
            Title = "Guardar como",
            FileName = suggestedName,
            AddExtension = true
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var editor = FindEditor(currentTab);
            if (editor == null) return;
            // Guardar como conserva la codificación del documento igual que Guardar.
            File.WriteAllText(dialog.FileName, editor.Text, currentTab.Encoding);
            currentTab.FilePath = dialog.FileName;
            currentTab.Title = Path.GetFileName(dialog.FileName);
            currentTab.IsModified = false;

            // Un archivo nuevo no tenía carpeta asociada al abrir la aplicación. Después
            // de "Guardar como", el explorador pasa a la carpeta elegida y selecciona
            // el archivo recién creado.
            if (Path.GetDirectoryName(dialog.FileName) is { } directory)
            {
                LoadExplorer(directory);
                SelectExplorerPath(dialog.FileName);
            }

            UpdateStatus($"Guardado: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"No se pudo guardar el archivo:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CloseTab()
    {
        var currentTab = Current;
        if (currentTab == null) return;
        if (ConfirmClose(currentTab)) RemoveTab(currentTab);
    }

    /// <summary>Pregunta por los cambios pendientes. Devuelve false si el usuario cancela.</summary>
    private bool ConfirmClose(TabItemModel tab)
    {
        if (!tab.IsModified) return true;

        var result = MessageBox.Show(
            $"¿Guardar los cambios en \"{tab.Title}\"?",
            "NoteChr", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

        switch (result)
        {
            case MessageBoxResult.No:
                return true;
            case MessageBoxResult.Cancel:
                return false;
            default:
                var control = MainTabControl.Items.OfType<TabItem>().FirstOrDefault(t => ReferenceEquals(t.Tag, tab));
                if (control == null) return false;

                // Hay que seleccionar la pestaña que se está guardando: al cerrar desde el
                // botón de la X, la activa puede ser otra y se guardaría el archivo
                // equivocado. Se silencia SelectionChanged para no alterar la barra de
                // estado con un mensaje de cambio de pestaña.
                _suppressSelectionEvent = true;
                try
                {
                    MainTabControl.SelectedItem = control;
                    SaveFile();
                }
                finally { _suppressSelectionEvent = false; }

                return !tab.IsModified;   // false si el usuario canceló el diálogo de guardado
        }
    }

    private void CloseTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TabItemModel tab }) return;
        if (ConfirmClose(tab)) RemoveTab(tab);
    }

    /// <summary>
    /// Se pide confirmación de las pestañas modificadas al cerrar la ventana. Sin esto,
    /// cerrar con la X descartaba los cambios sin avisar.
    /// </summary>
    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isClosing) return;

        _workspaceWatcher?.Dispose();
        _explorerRefreshTimer.Stop();

        // Se pregunta por todas antes de cerrar ninguna: si el usuario cancela en la
        // segunda pestaña, no debería haberse cerrado ya la primera. _isClosing se activa
        // antes de remover para que RemoveTab no abra una pestaña vacía al quedarse sin
        // ninguna, y se hace sobre una copia porque RemoveTab modifica _tabs.
        foreach (var tab in _tabs.ToList())
        {
            if (!ConfirmClose(tab))
            {
                e.Cancel = true;
                return;
            }
        }

        _isClosing = true;
        foreach (var tab in _tabs.ToList()) RemoveTab(tab);
    }

    #endregion

    #region Editor Creation

    private Border CreateEditor(string content = "")
    {
        var border = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Los números de línea viven en un Canvas desplazable propio: el TextBox no comparte
        // su ScrollViewer con ellos, así que hay que sincronizar el desplazamiento a mano.
        var lineNumbersHost = new Canvas
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF8, 0xF8, 0xF8)),
            Width = 50
        };

        var lineNumbersTransform = new TranslateTransform();
        var lineNumbers = new TextBlock
        {
            FontFamily = EditorFontFamily,
            FontSize = BaseFontSize * _zoomFactor,
            Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
            TextAlignment = TextAlignment.Left,
            Padding = new Thickness(8, 8, 8, 8),
            RenderTransform = lineNumbersTransform
        };
        Canvas.SetLeft(lineNumbers, 0);
        Canvas.SetTop(lineNumbers, 0);
        lineNumbersHost.Children.Add(lineNumbers);
        Grid.SetColumn(lineNumbersHost, 0);
        Grid.SetRowSpan(lineNumbersHost, 2);

        // Text editor
        var textBox = new TextBox
        {
            FontFamily = EditorFontFamily,
            FontSize = BaseFontSize * _zoomFactor,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8),
            Background = Brushes.Transparent,
            CaretBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)),
            SelectionBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xE8, 0xFF))
        };

        // Completion popup
        var completionPopup = new Popup
        {
            Placement = PlacementMode.Relative,
            PlacementTarget = textBox,
            StaysOpen = false,
            AllowsTransparency = true,
            // Si el Popup fuese focusable, el TextBox perdería el foco y Tab/Enter no llegarían.
            Focusable = false
        };

        var completionBorder = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            MaxHeight = 200,
            Width = 250,
            Effect = new DropShadowEffect
            {
                ShadowDepth = 2,
                BlurRadius = 8,
                Opacity = 0.3
            }
        };

        // Los elementos son cadenas, así que el ContentPresenter las muestra tal cual.
        // Con DisplayMemberPath el binding busca una propiedad llamada "." en un string
        // y falla, dejando la lista en blanco.
        var completionList = new ListBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            ItemContainerStyle = (Style)FindResource("CompletionItem")
        };
        completionBorder.Child = completionList;
        completionPopup.Child = completionBorder;

        // El Border lleva la referencia al editor: se rellena ahora, antes de que cualquier
        // manejador pueda necesitarla, en lugar de reconstruirla en AddTab.
        var reference = new EditorReference
        {
            TextBox = textBox,
            LineNumbers = lineNumbers,
            LineNumbersHost = lineNumbersHost,
            CompletionPopup = completionPopup
        };
        border.Tag = reference;

        // Desplazar los números de línea junto con el texto. El TextBox no expone
        // ScrollChanged, así que se busca su ScrollViewer interno en el árbol visual. Se
        // hace en Loaded porque antes la plantilla del TextBox todavía no se ha aplicado
        // y el árbol visual está vacío.
        textBox.Loaded += (_, _) =>
        {
            if (FindDescendant<ScrollViewer>(textBox) is { } editorScroller)
            {
                editorScroller.ScrollChanged += (_, _) =>
                    lineNumbersTransform.Y = -editorScroller.VerticalOffset;
            }

            // Al cambiar el tamaño cambia el alto de línea: hay que repintar los números
            // para que sigan alineados con el texto.
            textBox.SizeChanged += (_, _) => UpdateLineNumbers(reference, textBox);
            UpdateLineNumbers(reference, textBox);
        };

        // El cursor se actualiza después de TextChanged; abrir el popup aquí podía
        // desplazarlo en mitad de una pulsación y completar solo parte de la palabra.
        int completionVersion = 0;
        textBox.TextChanged += (s, e) =>
        {
            UpdateLineNumbers(reference, textBox);
            UpdatePosition(textBox);

            // El contenido del archivo se carga antes de que la referencia tenga propietario,
            // así que abrir un archivo no marca la pestaña. Todo lo posterior sí cuenta.
            if (reference.Owner is { } owner) owner.IsModified = true;

            int version = ++completionVersion;
            if (_suppressCompletion) return;

            textBox.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (version != completionVersion || !textBox.IsKeyboardFocusWithin ||
                    !ReferenceEquals(Current, reference.Owner)) return;

                var word = GetCurrentWord(textBox);
                var suggestions = word.Length >= 2 ? GetSuggestions(word) : new List<string>();
                if (suggestions.Count == 0)
                {
                    completionPopup.IsOpen = false;
                    return;
                }

                completionList.ItemsSource = suggestions;
                completionList.SelectedIndex = 0;
                ScrollIntoView(completionList);
                ShowCompletionPopup(textBox, completionPopup);
            });
        };

        // La posición del cursor cambia también al moverlo con flechas o al hacer clic,
        // sin pasar por TextChanged: hay que escucharlo aparte.
        textBox.SelectionChanged += (_, _) => UpdatePosition(textBox);

        // El contenido se asigna al final, con los manejadores ya registrados: así
        // TextChanged se dispara también al abrir un archivo y los números de línea y la
        // posición salen correctos desde el primer momento.
        textBox.Text = content;

        // PreviewKeyDown se ejecuta antes que el Manejador de entrada del TextBox, que es quien
        // consume Tab (AcceptsTab) y Enter: hay que actuar antes o la tecla se pierde.
        textBox.PreviewKeyDown += (s, e) =>
        {
            if (!completionPopup.IsOpen) return;

            switch (e.Key)
            {
                case Key.Down:
                    completionList.SelectedIndex = Math.Min(completionList.Items.Count - 1, completionList.SelectedIndex + 1);
                    ScrollIntoView(completionList);
                    e.Handled = true;
                    break;
                case Key.Up:
                    completionList.SelectedIndex = Math.Max(0, completionList.SelectedIndex - 1);
                    ScrollIntoView(completionList);
                    e.Handled = true;
                    break;
                case Key.Enter:
                case Key.Tab:
                    if (completionList.SelectedItem is string suggestion)
                    {
                        AcceptCompletion(textBox, completionList, completionPopup, suggestion);
                        e.Handled = true;
                    }
                    else completionPopup.IsOpen = false;
                    break;
                case Key.Escape:
                    completionPopup.IsOpen = false;
                    e.Handled = true;
                    break;
            }
        };

        // El popup se abre desde TextChanged, con el texto a medio cambiar. Recuperar el foco
        // en ese momento reposiciona el cursor y el carácter recién escrito se queda antes
        // de donde debería, así que solo se hace si el editor no lo tenía.
        completionPopup.Opened += (_, _) => FocusIfNeeded(textBox);

        // Aceptar con el ratón: el ListBox no puede quedarse con el foco, pero sí recibir clics.
        completionList.MouseUp += (_, _) =>
        {
            if (completionPopup.IsOpen && completionList.SelectedItem is string suggestion)
                AcceptCompletion(textBox, completionList, completionPopup, suggestion);
        };

        // Tras aceptar con el ratón el foco está en la lista, que va a desaparecer: hay que
        // devolverlo al editor para que siga recibiendo el teclado.
        // No devolver el foco al cerrar automáticamente: al cambiar de pestaña o
        // abrir el buscador, hacerlo se lo robaría al nuevo control.
        textBox.LostKeyboardFocus += (_, _) =>
        {
            if (completionPopup.IsOpen && !completionPopup.IsKeyboardFocusWithin)
                completionPopup.IsOpen = false;
        };

        Grid.SetRow(textBox, 0);
        Grid.SetColumn(textBox, 1);
        Grid.SetRowSpan(textBox, 2);

        grid.Children.Add(lineNumbersHost);
        grid.Children.Add(textBox);
        grid.Children.Add(completionPopup); // en el árbol para poder recuperarlo por referencia
        border.Child = grid;

        return border;
    }

    /// <summary>
    /// Acepta la sugerencia: cierra el popup antes de escribir para que el TextChanged
    /// no vuelva a lanzar el autocompletado sobre el texto recién insertado.
    /// </summary>
    private void AcceptCompletion(TextBox textBox, ListBox list, Popup popup, string suggestion)
    {
        popup.IsOpen = false;
        list.ItemsSource = null;

        var word = GetCurrentWord(textBox);
        if (word.Length == 0) return;

        var start = textBox.CaretIndex - word.Length;

        // Insertar la sugerencia dispara TextChanged, que volvería a abrir el popup sobre
        // la palabra recién insertada: hay que suprimirlo solo durante esta escritura.
        _suppressCompletion = true;
        try
        {
            textBox.Select(start, word.Length);
            textBox.SelectedText = suggestion;
            textBox.CaretIndex = start + suggestion.Length;
        }
        finally { _suppressCompletion = false; }
        FocusIfNeeded(textBox);
    }

    /// <summary>
    /// Coloca la lista de sugerencias bajo el cursor. Se desactiva el "stays open" para que no
    /// robe el foco: si el Popup tiene foco, el TextBox deja de recibir Tab y Enter.
    /// </summary>
    private static void ShowCompletionPopup(TextBox textBox, Popup popup)
    {
        // GetRectFromCharacterIndex lanza si el índice no cae dentro del texto, y devuelve
        // un rectángulo vacío mientras el TextBox todavía no tiene layout calculado: en ese
        // caso se abre igualmente, en el origen, en vez de no aparecer.
        try
        {
            if (textBox.CaretIndex >= 0 && textBox.CaretIndex <= textBox.Text.Length)
            {
                var caret = textBox.GetRectFromCharacterIndex(textBox.CaretIndex);
                popup.HorizontalOffset = caret.X;
                popup.VerticalOffset = caret.Bottom;
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            popup.HorizontalOffset = 0;
            popup.VerticalOffset = 0;
        }

        popup.IsOpen = true;
    }

    /// <summary>
    /// Dibuja los números de línea. La columna se ensancha con la cantidad de dígitos y con
    /// el zoom para que el número más largo no se corte.
    /// </summary>
    private static void UpdateLineNumbers(EditorReference reference, TextBox textBox)
    {
        // Antes de que el TextBox tenga layout, LineCount es 0: se cuenta a mano para que
        // el número de líneas sea correcto también en esa fase.
        var lineCount = textBox.LineCount;
        if (lineCount < 1)
        {
            lineCount = 1;
            var pending = textBox.Text;
            for (int i = 0; i < pending.Length; i++)
                if (pending[i] == '\n') lineCount++;
        }
        var digits = (int)Math.Floor(Math.Log10(lineCount)) + 1;

        // '\n' en vez de AppendLine: AppendLine añade un salto final que crea una línea
        // fantasma y descuadra la altura respecto al texto.
        var sb = new StringBuilder(lineCount * (digits + 1));
        for (int i = 1; i <= lineCount; i++)
        {
            if (i > 1) sb.Append('\n');
            sb.Append(i);
        }
        reference.LineNumbers.Text = sb.ToString();

        var charWidth = reference.LineNumbers.FontSize * 0.62;  // ancho aproximado de un dígito
        reference.LineNumbersHost.Width = Math.Max(44, 16 + digits * charWidth);
    }

    /// <summary>Mantiene visible el elemento seleccionado al recorrer las sugerencias con las flechas.</summary>
    private static void ScrollIntoView(ListBox list)
    {
        if (list.SelectedItem is not null) list.ScrollIntoView(list.SelectedItem);
    }

    /// <summary>
    /// Refleja el cursor en la barra de estado. La línea y la columna se calculan sobre el
    /// texto, no con GetLineIndexFromCharacterIndex: durante TextChanged esas APIs todavía
    /// no están sincronizadas con el contenido y devuelven índices fuera de rango.
    /// </summary>
    private void UpdatePosition(TextBox textBox)
    {
        var text = textBox.Text;
        var caret = Math.Clamp(textBox.CaretIndex, 0, text.Length);

        int line = 1, lineStart = 0;
        for (int i = 0; i < caret; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }

        PositionText.Text = $"Ln {line}, Col {caret - lineStart + 1}";
    }

    private string GetCurrentWord(TextBox textBox)
    {
        var text = textBox.Text;
        var pos = textBox.CaretIndex;
        if (pos > text.Length) pos = text.Length;

        int start = pos;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_'))
            start--;

        return text[start..pos];
    }

    /// <summary>
    /// Palabras ya escritas en las pestañas abiertas que empiezan por el prefijo. Se
    /// recorren los editores de uno en uno en vez de concatenar todo el texto: unirlo
    /// obligaba a copiar cada documento entero en memoria en cada pulsación.
    /// </summary>
    private List<string> GetSuggestions(string prefix)
    {
        var suggestions = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tab in _tabs)
        {
            var editor = FindEditor(tab);
            if (editor == null) continue;

            int scanned = 0;
            foreach (System.Text.RegularExpressions.Match match in WordRegex.Matches(editor.Text))
            {
                if (++scanned > MaxSuggestionScan) break;
                var candidate = match.Value;
                if (candidate.Length <= prefix.Length) continue;
                if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(candidate)) continue;

                suggestions.Add(candidate);
                if (suggestions.Count >= MaxSuggestions * 2) break;
            }

            // Con un máximo alcanzado ya no hace falta seguir mirando el resto de pestañas.
            if (suggestions.Count >= MaxSuggestions * 2) break;
        }

        suggestions.Sort(StringComparer.OrdinalIgnoreCase);
        return suggestions.GetRange(0, Math.Min(MaxSuggestions, suggestions.Count));
    }

    private static readonly System.Text.RegularExpressions.Regex WordRegex =
        new(@"[\p{L}\p{N}_]+", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Busca un descendiente del tipo indicado en el árbol visual.</summary>
    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static EditorReference? FindEditorReference(TabItemModel? tab) =>
        (tab?.Content as Border)?.Tag as EditorReference;

    private TextBox? FindEditor(TabItemModel? tab) => FindEditorReference(tab)?.TextBox;

    #endregion

    #region Keyboard Shortcuts

    /// <summary>
    /// Atajos globales. Se usa PreviewKeyDown porque el TextBox marca Tab como tecla
    /// gestionada (AcceptsTab) y en ese caso el evento no llega hasta la ventana.
    /// </summary>
    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (TryHandleShortcut(e.Key, Keyboard.Modifiers)) e.Handled = true;
    }

    /// <summary>
    /// Traduce una combinación de teclas a una acción. Devuelve true si el atajo se ha
    /// gestionado. Se separa del manejador de eventos para poder comprobarla directamente.
    /// </summary>
    private bool TryHandleShortcut(Key key, ModifierKeys modifiers)
    {
        var ctrl = modifiers.HasFlag(ModifierKeys.Control);
        var shift = modifiers.HasFlag(ModifierKeys.Shift);
        var alt = modifiers.HasFlag(ModifierKeys.Alt);

        // Ctrl+Alt corresponde a AltGr en muchos teclados europeos: no es un atajo nuestro,
        // así que se ignora para no interferir con la escritura de caracteres.
        if (alt) return false;

        if (ctrl && !shift)
        {
            switch (key)
            {
                case Key.N: NewFile(); return true;
                case Key.O: OpenFile(); return true;
                case Key.S: SaveFile(); return true;
                case Key.W: CloseTab(); return true;
                case Key.Tab: NextTab(); return true;
                case Key.F:
                case Key.H: ShowFindPanel(); return true;
                case Key.B: ToggleExplorer(); return true;
                case Key.D0:
                case Key.NumPad0: ZoomReset(); return true;
                case Key.OemPlus:
                case Key.Add: ZoomIn(); return true;
                case Key.OemMinus:
                case Key.Subtract: ZoomOut(); return true;
            }
        }
        else if (ctrl && shift)
        {
            switch (key)
            {
                case Key.S: SaveAs(); return true;
                case Key.Tab: PrevTab(); return true;
            }
        }
        else if (!ctrl && !shift)
        {
            if (key == Key.F1) { ShowHelp(); return true; }
            if (key == Key.Escape && FindPanel.Visibility == Visibility.Visible) { CloseFindPanel(); return true; }
        }

        return false;
    }

    #endregion

    #region Find/Replace

    private void ShowFindPanel()
    {
        FindPanel.Visibility = Visibility.Visible;
        FindTextBox.Focus();
        FindTextBox.SelectAll();
    }

    private void CloseFind_Click(object sender, RoutedEventArgs e) => CloseFindPanel();

    private void CloseFindPanel()
    {
        FindPanel.Visibility = Visibility.Collapsed;
        FocusEditor();   // si no, el foco se queda en un control invisible
    }

    /// <summary>
    /// Busca desde el cursor y, si no hay más coincidencias, vuelve al principio del
    /// documento. Devuelve false si no aparece la cadena.
    /// </summary>
    private bool FindNext(TextBox editor, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return false;

        // Si lo seleccionado ya es una coincidencia, la búsqueda continúa justo después:
        // de lo contrario volvería a seleccionar lo mismo una y otra vez. Si no, arranca
        // al principio de la selección, que es donde el usuario espera que empiece.
        int from = 0;
        if (editor.SelectionLength > 0 &&
            editor.SelectedText.Equals(needle, StringComparison.OrdinalIgnoreCase))
        {
            from = editor.SelectionStart + editor.SelectionLength;
        }
        else if (editor.SelectionStart > 0)
        {
            from = editor.SelectionStart;
        }

        if (from > editor.Text.Length) from = editor.Text.Length;

        var index = editor.Text.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase);

        // Segunda pasada desde el inicio para dar la vuelta al documento.
        if (index < 0) index = editor.Text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);

        if (index < 0) return false;

        editor.Focus();
        editor.Select(index, needle.Length);
        return true;
    }

    private void FindNext_Click(object sender, RoutedEventArgs e)
    {
        _findText = FindTextBox.Text;
        if (string.IsNullOrEmpty(_findText)) return;

        var editor = FindEditor(Current);
        if (editor == null) return;

        if (FindNext(editor, _findText)) UpdateStatus($"Encontrado: {_findText}");
        else UpdateStatus($"No encontrado: {_findText}");
    }

    /// <summary>
    /// Sustituye la coincidencia actualmente seleccionada, si la hay, y avanza a la
    /// siguiente. Si no hay ninguna seleccionada, solo avanza.
    /// </summary>
    private void Replace()
    {
        _findText = FindTextBox.Text;
        _replaceText = ReplaceTextBox.Text;

        var editor = FindEditor(Current);
        if (editor == null || string.IsNullOrEmpty(_findText)) return;

        bool replaced = false;

        // Solo se sustituye si lo seleccionado es exactamente una coincidencia: con otro
        // texto seleccionado, "Reemplazar" borraría lo que el usuario hubiera elegido.
        if (editor.SelectionLength == _findText.Length &&
            editor.SelectionLength > 0 &&
            editor.SelectedText.Equals(_findText, StringComparison.OrdinalIgnoreCase))
        {
            editor.SelectedText = _replaceText;
            replaced = true;
        }

        if (!replaced && !FindNext(editor, _findText))
        {
            UpdateStatus($"No encontrado: {_findText}");
            return;
        }

        UpdateStatus(replaced ? $"Reemplazado 1 vez · {_findText}" : $"No hay coincidencia seleccionada · {_findText}");
    }

    private void Replace_Click(object sender, RoutedEventArgs e) => Replace();

    private void ReplaceAll_Click(object sender, RoutedEventArgs e) => ReplaceAll();

    /// <summary>
    /// Sustituye todas las apariciones. Se construye el resultado en un StringBuilder en
    /// lugar de editar el texto en un bucle: si el texto buscado aparece dentro del
    /// reemplazo, editar en el sitio no avanzaba y el bucle no terminaba nunca.
    /// </summary>
    private void ReplaceAll()
    {
        _findText = FindTextBox.Text;
        _replaceText = ReplaceTextBox.Text;

        if (string.IsNullOrEmpty(_findText)) return;

        var editor = FindEditor(Current);
        if (editor == null) return;

        var text = editor.Text;
        var result = new StringBuilder(text.Length);
        int position = 0;
        int count = 0;

        while (position <= text.Length)
        {
            int index = text.IndexOf(_findText, position, StringComparison.OrdinalIgnoreCase);
            if (index < 0) break;

            result.Append(text, position, index - position);
            result.Append(_replaceText);

            // Se salta la coincidencia original, no lo insertado: así el reemplazo no se
            // vuelve a buscar a sí mismo.
            position = index + _findText.Length;
            count++;
        }

        result.Append(text, position, text.Length - position);

        // Sigue siendo una edición real (la pestaña debe marcarse como modificada),
        // pero el autocompletado no debe reaparecer sobre el texto recién escrito.
        _suppressCompletion = true;
        try { editor.Text = result.ToString(); }
        finally { _suppressCompletion = false; }

        UpdateStatus(count == 0 ? $"No encontrado: {_findText}" : $"Reemplazado {count} {(count == 1 ? "vez" : "veces")}");
    }

    #endregion

    #region Menu Click Handlers

    private void NewFile_Click(object sender, RoutedEventArgs e) => NewFile();
    private void OpenFile_Click(object sender, RoutedEventArgs e) => OpenFile();
    private void SaveFile_Click(object sender, RoutedEventArgs e) => SaveFile();
    private void SaveAs_Click(object sender, RoutedEventArgs e) => SaveAs();
    private void CloseTab_Click(object sender, RoutedEventArgs e) => CloseTab();
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Undo_Click(object sender, RoutedEventArgs e) => FindEditor(Current)?.Undo();

    private void Redo_Click(object sender, RoutedEventArgs e) => FindEditor(Current)?.Redo();

    private void Cut_Click(object sender, RoutedEventArgs e) => FindEditor(Current)?.Cut();

    private void Copy_Click(object sender, RoutedEventArgs e) => FindEditor(Current)?.Copy();

    private void Paste_Click(object sender, RoutedEventArgs e) => FindEditor(Current)?.Paste();

    private void SelectAll_Click(object sender, RoutedEventArgs e) => FindEditor(Current)?.SelectAll();

    private void Find_Click(object sender, RoutedEventArgs e) => ShowFindPanel();
    private void ReplaceMenu_Click(object sender, RoutedEventArgs e) => ShowFindPanel();

    private void NextTab_Click(object sender, RoutedEventArgs e) => NextTab();
    private void PrevTab_Click(object sender, RoutedEventArgs e) => PrevTab();

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomIn();

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomOut();

    private void ZoomReset_Click(object sender, RoutedEventArgs e) => ZoomReset();

    private void ZoomIn()
    {
        // Redondear evita que 1.0 + 0.1 deje valores como 1.1000000000000001.
        _zoomFactor = Math.Round(Math.Min(MaxZoom, _zoomFactor + ZoomStep), 2);
        ApplyZoom();
    }

    private void ZoomOut()
    {
        _zoomFactor = Math.Round(Math.Max(MinZoom, _zoomFactor - ZoomStep), 2);
        ApplyZoom();
    }

    private void ZoomReset()
    {
        _zoomFactor = 1.0;
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        ZoomText.Text = $"{(int)(_zoomFactor * 100)}%";
        foreach (var tab in _tabs)
        {
            if (FindEditorReference(tab) is not { } reference) continue;

            // El zoom afecta también a los números de línea: si no, dejan de alinearse
            // con el texto al que corresponden.
            var size = BaseFontSize * _zoomFactor;
            reference.TextBox.FontSize = size;
            reference.LineNumbers.FontSize = size;
            UpdateLineNumbers(reference, reference.TextBox);
        }
    }

    private void Help_Click(object sender, RoutedEventArgs e) => ShowHelp();

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            $"NoteChr v1.0\n\nEditor de texto de escritorio con pestañas, autocompletado y barra de estado.\n" +
            $"Windows 10 o superior · .NET 8 · WPF\n\n" +
            $"Atajos de teclado con F1.",
            "Acerca de NoteChr", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ShowHelp()
    {
        var helpText = @"Atajos de teclado de NoteChr

Archivo:
  Ctrl+N          Nuevo archivo
  Ctrl+O          Abrir archivo
  Ctrl+S          Guardar
  Ctrl+Shift+S    Guardar como
  Ctrl+W          Cerrar pestaña

Edición:
  Ctrl+Z          Deshacer
  Ctrl+Y          Rehacer
  Ctrl+X          Cortar
  Ctrl+C          Copiar
  Ctrl+V          Pegar
  Ctrl+A          Seleccionar todo
  Ctrl+F          Buscar
  Ctrl+H          Reemplazar

Navegación:
  Ctrl+Tab        Pestaña siguiente
  Ctrl+Shift+Tab  Pestaña anterior

Ver:
  Ctrl++          Zoom +
  Ctrl+-          Zoom -
  Ctrl+0          Restablecer zoom

Ayuda:
  F1              Mostrar ayuda

Buscar y reemplazar:
  Ctrl+F          Buscar
  Ctrl+H          Reemplazar
  Enter           Buscar siguiente
  Mayús+Enter     Reemplazar
  Esc             Cerrar el panel

Autocompletado:
  Escribe 2+ caracteres y aparecerán sugerencias
  ↑/↓             Navegar sugerencias
  Enter/Tab       Aceptar sugerencia
  Esc             Cancelar

Nota: los archivos se guardan en UTF-8 sin BOM, conservando la
codificación original si el archivo ya usaba otra.";
        MessageBox.Show(helpText, "Atajos de teclado", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    #endregion

    #region Tab Navigation

    private void NextTab()
    {
        if (_tabs.Count == 0 || MainTabControl.Items.Count == 0) return;
        var currentIndex = MainTabControl.SelectedIndex;

        // SelectedIndex es -1 si no hay nada seleccionado: con el módulo daría un
        // resultado negativo o saltaría la primera pestaña.
        var next = currentIndex < 0 ? 0 : (currentIndex + 1) % MainTabControl.Items.Count;
        MainTabControl.SelectedIndex = next;
    }

    private void PrevTab()
    {
        if (_tabs.Count == 0) return;
        var currentIndex = MainTabControl.SelectedIndex;
        // SelectedItem esperaba un elemento, no un índice: asignarle un int no cambiaba nada.
        MainTabControl.SelectedIndex = (currentIndex - 1 + _tabs.Count) % _tabs.Count;
    }

    /// <summary>
    /// Tras eliminar la pestaña activa, WPF selecciona otra, pero si no queda ninguna
    /// SelectedIndex queda en -1 y la navegación entre pestañas calcularía un índice inválido.
    /// </summary>
    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionEvent) return;

        var tab = Current;
        if (tab == null) return;

        FocusEditor(tab);
        UpdateStatus($"Pestaña: {tab.Title}");

        // Cada pestaña tiene su propia posición de cursor y su codificación: al cambiar de
        // pestaña, la barra de estado seguía mostrando los datos de la anterior.
        EncodingText.Text = tab.EncodingLabel;
        if (FindEditorReference(tab) is { } reference)
        {
            UpdatePosition(reference.TextBox);
            reference.CompletionPopup.IsOpen = false;
        }
    }

    #endregion

    #region Helpers

    private void UpdateStatus(string message)
    {
        StatusText.Text = message;
    }

    #endregion
}

public class TabItemModel : INotifyPropertyChanged
{
    private string _title = "";
    private bool _isModified;

    public string Title
    {
        get => _title;
        set { _title = value; RefreshHeader(); }
    }

    /// <summary>Texto de la pestaña: nombre del archivo con un asterisco si hay cambios sin guardar.</summary>
    public string Header => IsModified ? $"{Title} *" : Title;

    /// <summary>Etiqueta de la pestaña, para actualizarla sin depender de bindings.</summary>
    public TextBlock? HeaderBlock { get; set; }

    public object Content { get; set; } = null!;
    public string? FilePath { get; set; }

    /// <summary>Codificación del archivo: se conserva al guardar para no corromperlo.</summary>
    public Encoding Encoding { get; set; } = new UTF8Encoding(false);

    /// <summary>Nombre de la codificación, mostrado en la barra de estado.</summary>
    public string EncodingLabel { get; set; } = "UTF-8";

    public bool IsModified
    {
        get => _isModified;
        set
        {
            if (_isModified == value) return;
            _isModified = value;
            RefreshHeader();
        }
    }

    private void RefreshHeader()
    {
        OnPropertyChanged(nameof(Header));
        if (HeaderBlock != null) HeaderBlock.Text = Header;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Contenido de un archivo junto con la codificación con la que se leyó.</summary>
public readonly record struct TextFile(string Text, Encoding Encoding, string Label);

public sealed class ThemeDefinition
{
    public string Id { get; set; } = "classic";
    public string Name { get; set; } = "Clásico NoteChr";
    public string Background { get; set; } = "#F5F5F5";
    public string Surface { get; set; } = "#FFFFFF";
    public string Border { get; set; } = "#E0E0E0";
    public string TextPrimary { get; set; } = "#1A1A1A";
    public string TextSecondary { get; set; } = "#666666";
    public string Accent { get; set; } = "#0078D4";
    public string AccentHover { get; set; } = "#1A86D9";
    public string AccentPressed { get; set; } = "#005A9E";
    public string Selection { get; set; } = "#CCE8FF";
    public string MenuHover { get; set; } = "#E5F3FF";
    public string MenuPressed { get; set; } = "#CCE8FF";
    public string TabBackground { get; set; } = "#E8E8E8";
    public string TabActive { get; set; } = "#FFFFFF";
    public string StatusBar { get; set; } = "#F0F0F0";
    public string EditorBackground { get; set; } = "#FFFFFF";
    public string EditorForeground { get; set; } = "#1A1A1A";
    public string LineNumber { get; set; } = "#999999";
    public string Caret { get; set; } = "#0078D4";
}

public class EditorReference
{
    public TextBox TextBox { get; set; } = null!;
    public TextBlock LineNumbers { get; set; } = null!;
    /// <summary>Contenedor de los números de línea, para poder ajustar su anchura.</summary>
    public Canvas LineNumbersHost { get; set; } = null!;
    public Popup CompletionPopup { get; set; } = null!;
    /// <summary>Pestaña a la que pertenece este editor, para marcarla al modificarse.</summary>
    public TabItemModel? Owner { get; set; }
}
