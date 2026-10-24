using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MKVision.Desktop;

// Baixa vídeos do YouTube pedidos pela extensão, com o yt-dlp (o YouTube embaralha os endereços do vídeo;
// o yt-dlp resolve isso rodando o código do site no Deno). Na primeira vez baixa os dois das páginas
// oficiais no GitHub, conferindo o SHA-256 publicado; o yt-dlp se atualiza a cada 7 dias (o YouTube muda
// com frequência). Só a imagem, na melhor qualidade (sem ffmpeg: o áudio do YouTube vem separado).
internal sealed partial class VideoDownload
{
    private static readonly string ToolsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MKVisionDesktop", "tools");
    private static readonly string YtDlp = Path.Combine(ToolsDir, "yt-dlp.exe");
    private static readonly string Deno = Path.Combine(ToolsDir, "deno.exe");

    private const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    private const string YtDlpSums = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS";
    private const string DenoUrl = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";
    private const string DenoSum = DenoUrl + ".sha256sum";

    // Progresso para a extensão: (percentual 0–100 ou -1, texto).
    public delegate void Report(double percent, string text);

    private readonly object _lock = new();
    private Process? _process;
    private CancellationTokenSource? _cancel;

    public bool Busy { get { lock (_lock) return _cancel is not null; } }

    public void Cancel()
    {
        lock (_lock)
        {
            _cancel?.Cancel();
            try { _process?.Kill(entireProcessTree: true); } catch { }
        }
    }

    // Devolve o caminho do arquivo salvo.
    public async Task<string> Run(string url, Report report)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !Regex.IsMatch(uri.Host, @"(^|\.)(youtube\.com|youtu\.be)$"))
            throw new InvalidOperationException("endereço que não é do YouTube");
        var cancel = new CancellationTokenSource();
        lock (_lock)
        {
            if (_cancel is not null) throw new InvalidOperationException("já tem um download em andamento");
            _cancel = cancel;
        }
        try
        {
            await EnsureTools(report, cancel.Token);
            return await Download(uri.AbsoluteUri, report, cancel.Token);
        }
        finally
        {
            lock (_lock) { _cancel = null; _process = null; }
        }
    }

    // ---------- Ferramentas ----------

    // O yt-dlp avisou que pode estar desatualizado (os sites mudam; a correção sai numa versão nova): atualiza na hora,
    // no máximo uma vez a cada 6 horas. true = conferiu (vale tentar de novo).
    private static long _updatedNowAt;

    public static bool LooksOutdated(string error) =>
        error.Contains("latest version", StringComparison.OrdinalIgnoreCase) || error.Contains("please report this issue", StringComparison.OrdinalIgnoreCase);

    public async Task<bool> UpdateNow(CancellationToken token)
    {
        long now = Environment.TickCount64;
        if (_updatedNowAt != 0 && now - _updatedNowAt < 6 * 3600_000L) return false;
        _updatedNowAt = now;
        if (!File.Exists(YtDlp)) return false;
        var (_, output) = await RunTool(YtDlp, ["-U"], null, token);
        File.SetLastWriteTimeUtc(YtDlp, DateTime.UtcNow);
        Log.Write($"Download de vídeo: yt-dlp -U (pedido pelo erro) → {output.Trim().Split('\n').LastOrDefault()?.Trim()}");
        return true;
    }

    private async Task EnsureTools(Report report, CancellationToken token)
    {
        Directory.CreateDirectory(ToolsDir);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MKVisionDesktop");

        if (!File.Exists(YtDlp))
        {
            report(-1, "Instalando o yt-dlp (só na primeira vez)...");
            var sums = await http.GetStringAsync(YtDlpSums, token);
            var expected = Regex.Match(sums, @"^([0-9a-f]{64})\s+\*?yt-dlp\.exe$", RegexOptions.Multiline).Groups[1].Value;
            await Fetch(http, YtDlpUrl, YtDlp, expected, token);
            Log.Write("Download de vídeo: yt-dlp instalado.");
        }
        else if (DateTime.UtcNow - File.GetLastWriteTimeUtc(YtDlp) > TimeSpan.FromDays(7))
        {
            report(-1, "Atualizando o yt-dlp...");
            var (code, output) = await RunTool(YtDlp, ["-U"], null, token);
            File.SetLastWriteTimeUtc(YtDlp, DateTime.UtcNow); // mesmo sem versão nova: confere de novo daqui a 7 dias
            Log.Write($"Download de vídeo: yt-dlp -U → {output.Trim().Split('\n').LastOrDefault()?.Trim()}");
        }

        if (!File.Exists(Deno))
        {
            report(-1, "Instalando o Deno, que o yt-dlp usa no YouTube (só na primeira vez)...");
            var sum = await http.GetStringAsync(DenoSum, token);
            var expected = Regex.Match(sum, @"[0-9a-fA-F]{64}").Value.ToLowerInvariant();
            string zip = Deno + ".zip";
            await Fetch(http, DenoUrl, zip, expected, token);
            using (var archive = ZipFile.OpenRead(zip))
            {
                var entry = archive.Entries.First(e => e.Name.Equals("deno.exe", StringComparison.OrdinalIgnoreCase));
                entry.ExtractToFile(Deno + ".tmp", overwrite: true);
            }
            File.Move(Deno + ".tmp", Deno, overwrite: true);
            File.Delete(zip);
            Log.Write("Download de vídeo: Deno instalado.");
        }
    }

    // Baixa para um arquivo temporário, confere o SHA-256 e só então põe no lugar.
    private static async Task Fetch(HttpClient http, string url, string path, string expectedSha256, CancellationToken token)
    {
        if (expectedSha256.Length != 64) throw new InvalidOperationException($"não achei o SHA-256 publicado de {Path.GetFileName(path)}");
        string temp = path + ".download";
        await using (var source = await http.GetStreamAsync(url, token))
        await using (var file = File.Create(temp))
            await source.CopyToAsync(file, token);
        string actual;
        await using (var file = File.OpenRead(temp))
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token));
        if (actual != expectedSha256)
        {
            File.Delete(temp);
            throw new InvalidOperationException($"{Path.GetFileName(path)} veio diferente do publicado (SHA-256 não confere)");
        }
        File.Move(temp, path, overwrite: true);
    }

    // ---------- Download ----------

    [GeneratedRegex(@"^MKV\|\s*([\d.]+)%\|(.*)\|(.*)$")]
    private static partial Regex ProgressLine();

    private async Task<string> Download(string url, Report report, CancellationToken token)
    {
        string folder = Path.Combine(DownloadsFolder(), "MK Vision");
        Directory.CreateDirectory(folder);
        report(-1, "Procurando a melhor qualidade...");
        string? saved = null;
        string? error = null;
        var (code, _) = await RunTool(YtDlp,
        [
            "--js-runtimes", $"deno:{Deno}",
            "--no-playlist", "--no-mtime", "--encoding", "utf-8",
            "-f", "bv*/b",                       // melhor imagem (vídeo sem áudio no YouTube: sem ffmpeg para juntar)
            "-P", folder,
            "-o", "%(title).90B (%(height)sp).%(ext)s",
            "--newline", "--progress",
            "--progress-template", "download:MKV|%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s",
            "--print", "after_move:MKVFILE|%(filepath)s",
            "--", url
        ], line =>
        {
            var m = ProgressLine().Match(line);
            if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out double percent))
                report(percent, $"{m.Groups[2].Value.Trim()} · faltam {m.Groups[3].Value.Trim()}");
            else if (line.StartsWith("MKVFILE|")) saved = line[8..].Trim();
            else if (line.StartsWith("ERROR:")) error = line[6..].Trim();
        }, token);
        token.ThrowIfCancellationRequested();
        if (code != 0 || saved is null) throw new InvalidOperationException(error ?? $"o yt-dlp terminou com código {code}");
        return saved;
    }

    private async Task<(int code, string output)> RunTool(string exe, string[] args, Action<string>? onLine, CancellationToken token)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = ToolsDir
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        var output = new StringBuilder();
        using var process = new Process { StartInfo = info };
        void OnData(object _, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            lock (output) output.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        }
        process.OutputDataReceived += OnData;
        process.ErrorDataReceived += OnData;
        process.Start();
        lock (_lock) _process = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException) { try { process.Kill(entireProcessTree: true); } catch { } throw; }
        process.WaitForExit(); // termina de ler a saída
        return (process.ExitCode, output.ToString());
    }

    // Pasta Downloads do usuário (pode ter sido movida para outro lugar).
    public static string DownloadsFolder()
    {
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var path) == 0)
        {
            try { return Marshal.PtrToStringUni(path)!; }
            finally { Marshal.FreeCoTaskMem(path); }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
}
