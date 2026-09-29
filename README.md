# NoteChr

NoteChr es un editor de texto de escritorio para Windows, creado con C#,
.NET 8 y WPF. Incluye pestañas, explorador de carpetas, autocompletado,
temas visuales, búsqueda y reemplazo, zoom y detección de cambios en los
directorios abiertos.

## Características

- Editor con pestañas y aviso de cambios sin guardar.
- Explorador lateral tipo Visual Studio Code.
  - Redimensionable arrastrando el separador.
  - Ocultable con el botón o `Ctrl+B`.
  - Actualización automática al cambiar archivos o carpetas.
  - Doble clic para abrir archivos.
- Abrir archivos y carpetas desde el menú contextual de Windows.
- Preservación de UTF-8, UTF-8 con BOM, UTF-16 e ISO-8859-1.
- Autocompletado basado en las pestañas abiertas.
- Buscar, reemplazar y reemplazar todo.
- Seis temas JSON:
  - Clásico NoteChr
  - Dark Modern
  - Warm & Cozy
  - Synthwave / Cyberpunk
  - Minimalist Paper
  - Pure OLED Dark
- No incluye telemetría ni servicios en segundo plano.

## Requisitos

- Windows 10 o posterior, 64 bits.
- El instalador es autocontenido: no requiere instalar .NET por separado.

## Instalación recomendada

Descarga el instalador `NoteChr-1.0.11.0.msi` desde la sección **Releases**
de GitHub y ejecútalo. También puedes instalarlo desde PowerShell:

```powershell
msiexec /i ".\installer\NoteChr-1.0.11.0.msi"
```

El instalador permite elegir la carpeta, crea un acceso directo en el menú
Inicio y ofrece opcionalmente un acceso directo en el escritorio. También
añade las opciones `Abrir archivo con NoteChr` y `Abrir carpeta con NoteChr`.
No cambia silenciosamente la aplicación predeterminada de tus extensiones.

Para desinstalar:

```powershell
msiexec /x ".\installer\NoteChr-1.0.11.0.msi"
```

El instalador muestra unos términos breves y no intrusivos: no hay telemetría,
los archivos solo se leen o escriben cuando el usuario lo solicita y el
programa se entrega sin garantía. El texto completo está en
`installer/terms.rtf`.

## Ejecutar desde el código fuente

Requiere el SDK de .NET 8:

```powershell
dotnet run
```

## Crear una Release

Instala WiX 5 una sola vez:

```powershell
dotnet tool install --global wix --version 5.0.2
wix extension add WixToolset.UI.wixext/5.0.2 --global
```

Ejecuta las pruebas de Release y genera el MSI:

```powershell
.\tests\verify-release.ps1
.\installer\build-installer.ps1
```

El instalador se genera en `installer/NoteChr-<version>.msi`.

## Temas personalizados

Los temas están en `Themes/themes.json`. Añade otro objeto al array siguiendo
esta estructura:

```json
{
  "Id": "mi-tema",
  "Name": "Mi tema",
  "Background": "#1E1E2E",
  "Surface": "#181825",
  "Border": "#313244",
  "TextPrimary": "#CDD6F4",
  "TextSecondary": "#6C7086",
  "Accent": "#CBA6F7",
  "AccentHover": "#D8B4FE",
  "AccentPressed": "#A580D8",
  "Selection": "#45475A",
  "MenuHover": "#2B2B3D",
  "MenuPressed": "#45475A",
  "TabBackground": "#181825",
  "TabActive": "#1E1E2E",
  "StatusBar": "#181825",
  "EditorBackground": "#1E1E2E",
  "EditorForeground": "#CDD6F4",
  "LineNumber": "#6C7086",
  "Caret": "#89B4FA"
}
```

Después recompila para incluirlo en el MSI.

## Atajos

| Tecla | Acción |
| --- | --- |
| `Ctrl+N` | Nuevo archivo |
| `Ctrl+O` | Abrir archivo |
| `Ctrl+S` | Guardar |
| `Ctrl+Shift+S` | Guardar como |
| `Ctrl+W` | Cerrar pestaña |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Cambiar de pestaña |
| `Ctrl+B` | Mostrar u ocultar explorador |
| `Ctrl+F` | Buscar |
| `Ctrl+H` | Reemplazar |
| `Ctrl++` / `Ctrl+-` / `Ctrl+0` | Zoom |
| `F1` | Ayuda |

## Licencia

NoteChr se distribuye bajo la licencia MIT. Consulta `LICENSE`.
