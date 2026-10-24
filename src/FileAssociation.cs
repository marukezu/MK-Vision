using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MKVision.Desktop;

// Registra o MK Vision como um app que abre vídeos (só no usuário atual, HKCU): aparece no "Abrir com" do
// Explorer e em Configurações → Aplicativos padrão, onde a pessoa escolhe torná-lo o padrão (o Windows não
// deixa um programa se colocar como padrão sozinho). Desfazer apaga tudo o que foi criado aqui.
internal static class FileAssociation
{
    public static readonly string[] Extensions = [".mp4", ".m4v", ".mkv", ".webm", ".mov", ".ts", ".avi", ".wmv"];

    private const string ProgId = "MKVision.Video";
    private const string AppName = "MK Vision";
    private const string CapabilitiesKey = @"Software\MKVision\Capabilities";
    private static string ExeName => Path.GetFileName(Environment.ProcessPath!);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command");
        return key?.GetValue("") is string command && command.Contains(Environment.ProcessPath!, StringComparison.OrdinalIgnoreCase);
    }

    public static void Register()
    {
        string exe = Environment.ProcessPath!;
        string open = $"\"{exe}\" \"%1\"";
        using (var progId = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            progId.SetValue("", "Vídeo (MK Vision)");
            using (var icon = progId.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{exe}\",0");
            using (var command = progId.CreateSubKey(@"shell\open\command")) command.SetValue("", open);
        }
        using (var app = Registry.CurrentUser.CreateSubKey($@"Software\Classes\Applications\{ExeName}"))
        {
            app.SetValue("FriendlyAppName", AppName);
            using (var command = app.CreateSubKey(@"shell\open\command")) command.SetValue("", open);
            using var types = app.CreateSubKey("SupportedTypes");
            foreach (var ext in Extensions) types.SetValue(ext, "");
        }
        using (var capabilities = Registry.CurrentUser.CreateSubKey(CapabilitiesKey))
        {
            capabilities.SetValue("ApplicationName", AppName);
            capabilities.SetValue("ApplicationDescription", "Player com nitidez, IA de ampliação e câmera lenta suave");
            using var associations = capabilities.CreateSubKey("FileAssociations");
            foreach (var ext in Extensions) associations.SetValue(ext, ProgId);
        }
        using (var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            registered.SetValue(AppName, CapabilitiesKey);
        foreach (var ext in Extensions)
            using (var openWith = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\OpenWithProgids"))
                openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        Notify();
        Log.Write($"MK Vision registrado como player de vídeos ({string.Join(" ", Extensions)}).");
    }

    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", false);
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\Applications\{ExeName}", false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\MKVision", false);
        using (var registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", true))
            registered?.DeleteValue(AppName, false);
        foreach (var ext in Extensions)
            using (var openWith = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ext}\OpenWithProgids", true))
                openWith?.DeleteValue(ProgId, false);
        Notify();
        Log.Write("MK Vision removido dos players de vídeo.");
    }

    // Abre a tela do Windows onde a pessoa escolhe o MK Vision como padrão.
    public static void OpenDefaultAppsSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo($"ms-settings:defaultapps?registeredAppUser={Uri.EscapeDataString(AppName)}") { UseShellExecute = true });
        }
        catch
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
        }
    }

    // Avisa o Explorer que as associações mudaram.
    private static void Notify() => SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);
}
