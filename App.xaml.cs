using System.IO;
using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace NoteChr;

public partial class App : Application
{
    /// <summary>
    /// Los archivos llegan por linea de comandos cuando el usuario hace doble clic o elige
    /// "Abrir con". Sin esto, NoteChr se abriria vacio.
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Los archivos llegan por linea de comandos al hacer doble clic o elegir "Abrir con".
        // Los que no existen se ignoran con un aviso, en vez de descartarlos en silencio.
        var files = new List<string>();
        var folders = new List<string>();
        foreach (var arg in e.Args)
        {
            if (File.Exists(arg)) files.Add(arg);
            else if (Directory.Exists(arg)) folders.Add(arg);
            else if (!string.IsNullOrWhiteSpace(arg))
                MessageBox.Show($"No se encontró el archivo:\n{arg}", "NoteChr", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        var window = new MainWindow();
        window.OpenFilesOnStartup(files);
        foreach (var folder in folders) window.OpenFolderOnStartup(folder);
        MainWindow = window;
        window.Show();
    }
}
