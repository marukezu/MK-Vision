namespace MKVision.Desktop;

// Mensagens do app: no console e em %LOCALAPPDATA%\MKVisionDesktop\log.txt (para diagnóstico). O da vez anterior
// fica em log-anterior.txt (um problema da sessão passada não se perde ao abrir de novo). Os autotestes gravam em
// outro arquivo (MKV_LOG), para não apagar o do app em uso.
internal static class Log
{
    private static readonly object Lock = new();
    public static readonly string FilePath = Environment.GetEnvironmentVariable("MKV_LOG") is { Length: > 0 } test ? test
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MKVisionDesktop", "log.txt");

    static Log()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        try
        {
            if (File.Exists(FilePath)) File.Copy(FilePath, Path.Combine(Path.GetDirectoryName(FilePath)!, Path.GetFileNameWithoutExtension(FilePath) + "-anterior.txt"), overwrite: true);
        }
        catch { } // outra janela do app com o arquivo aberto
        File.WriteAllText(FilePath, $"MK Vision — {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}");
    }

    // Privacidade: endereços da internet e caminhos de arquivo (inclusive dentro de mensagens de erro) não vão para o
    // arquivo — só o necessário para diagnosticar.
    private static readonly System.Text.RegularExpressions.Regex Private = new(
        @"\b(https?|wss?|blob|chrome-extension)://\S+|\b[A-Za-z]:\\[^\r\n""'<>|]*|\\\\[^\s""']+",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public static void Write(string message)
    {
        message = Private.Replace(message, m => m.Value.Contains("://") ? "[endereço]" : "[arquivo]");
        Console.WriteLine(message);
        lock (Lock)
        {
            try { File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}"); }
            catch { }
        }
    }
}
