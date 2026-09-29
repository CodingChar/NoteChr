using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NoteChr;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try { Run(); return 0; }
        catch (Exception ex) { Console.Error.WriteLine($"ERROR: {ex.Message}"); return 1; }
    }

    private static void Run()
    {
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var file = Path.Combine(AppContext.BaseDirectory, "Themes", "themes.json");
        var original = File.ReadAllText(file);
        try
        {
            foreach (var theme in System.Text.Json.JsonSerializer.Deserialize<ThemeDefinition[]>(original)!)
            {
                foreach (var background in new[] { theme.Surface, theme.Background, theme.MenuHover, theme.MenuPressed, theme.Selection, theme.TabBackground, theme.TabActive, theme.StatusBar })
                {
                    Contrast(theme.Id, theme.TextPrimary, background);
                    Contrast(theme.Id, theme.TextSecondary, background);
                }
                Contrast(theme.Id, theme.EditorForeground, theme.EditorBackground);
                Contrast(theme.Id, theme.EditorForeground, theme.Selection);
                Contrast(theme.Id, theme.LineNumber, theme.EditorBackground);
            }
            Console.WriteLine("OK: 114 pares de contraste de texto >= 4.5:1 en los seis temas.");
            Verify(6); // JSON distribuido
            File.Delete(file);
            Verify(6); // Respaldo integrado sin carpeta Themes
            File.WriteAllText(file, "[{\"Id\":\"custom\",\"Name\":\"Personalizado\",\"Background\":\"#123456\"}]");
            Verify(1); // Nuevo tema sin recompilar
            File.WriteAllText(file, "JSON roto");
            Verify(1); // Error recuperable
            Console.WriteLine("OK: JSON, respaldo, tema personalizado y JSON invalido; colores y nuevas pestanas verificados.");
        }
        finally { File.WriteAllText(file, original); app.Shutdown(); }
    }

    private static void Verify(int expected)
    {
        var window = new MainWindow();
        window.OpenFilesOnStartup(Array.Empty<string>());
        var menu = (MenuItem)window.FindName("ThemeMenu");
        window.Show();
        var tree = (TreeView)window.FindName("ExplorerTree");
        var folder = new TreeViewItem { Header = "Carpeta", IsExpanded = true };
        var child = new TreeViewItem { Header = "Archivo.txt" };
        folder.Items.Add(child);
        tree.Items.Add(folder);
        window.UpdateLayout();
        if (menu.Items.Count != expected) throw new Exception($"Temas: {menu.Items.Count}, esperados: {expected}");
        foreach (MenuItem item in menu.Items)
        {
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
            var theme = (ThemeDefinition)typeof(MainWindow).GetField("_currentTheme", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            foreach (var node in new[] { folder, child })
            {
                node.ApplyTemplate();
                if (((SolidColorBrush)node.Foreground).Color != (Color)ColorConverter.ConvertFromString(theme.TextPrimary))
                    throw new Exception("Texto del explorador no hereda el tema");
                node.IsSelected = true;
                var treeRow = (Border)node.Template.FindName("TreeRow", node);
                if (((SolidColorBrush)treeRow.Background).Color != (Color)ColorConverter.ConvertFromString(theme.Selection))
                    throw new Exception("Seleccion del explorador no usa el tema");
                node.IsSelected = false;
            }
            if (((SolidColorBrush)window.Background).Color.ToString() != ((Color)ColorConverter.ConvertFromString(theme.Background)).ToString())
                throw new Exception("El tema no cambio el fondo");
            menu.ApplyTemplate();
            var surface = (Border)menu.Template.FindName("SubmenuSurface", menu);
            if (((SolidColorBrush)surface.Background).Color != (Color)ColorConverter.ConvertFromString(theme.Surface))
                throw new Exception("El submenu conserva un fondo ajeno al tema");
            item.ApplyTemplate();
            if (item.Template.FindName("Row", item) is not Border row || row.BorderThickness != new Thickness(0))
                throw new Exception("Fila de menu sin plantilla tematica");
            typeof(MainWindow).GetMethod("NewFile", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
            var tabs = (TabControl)window.FindName("MainTabControl");
            foreach (TabItem tab in tabs.Items)
            {
                var reference = (EditorReference)((Border)tab.Content).Tag;
                if (((SolidColorBrush)reference.TextBox.Foreground).Color != (Color)ColorConverter.ConvertFromString(theme.EditorForeground))
                    throw new Exception("El tema no actualizo el editor");
            }
        }
        window.Close();
    }

    private static void Contrast(string id, string foreground, string background)
    {
        static double Luminance(string hex)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            static double Linear(byte b) { var s = b / 255d; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
        }
        var a = Luminance(foreground);
        var b = Luminance(background);
        var ratio = (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
        if (ratio < 4.5) throw new Exception($"Contraste insuficiente {id}: {foreground}/{background} = {ratio:F2}:1");
    }
}
