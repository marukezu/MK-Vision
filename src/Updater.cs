using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

namespace MKVision.Desktop;

// Atualização automática pelas Releases do GitHub (cada versão publicada lá tem o .zip do app). Ao abrir, o app
// pergunta ao GitHub se há versão mais nova (inclusive beta); havendo, avisa, e o "Atualizar" baixa o .zip, separa a
// pasta numa pasta temporária e, com o app fechado, um script copia por cima da pasta do app (o que não vem no .zip,
// como a IA de câmera lenta treinada, fica) e abre de novo. Os dados ficam em %LOCALAPPDATA%\MKVisionDesktop, longe
// disso. Só na pasta do app (a portátil): rodando da pasta de compilação (bin), não procura.
internal static class Updater
{
    public const string Repo = "marukezu/MK-Vision";
    public const string Page = "https://github.com/" + Repo + "/releases";

    public sealed record Release(string Version, string ZipUrl, long Size, string Notes);

    // A versão deste app (a do .csproj, ou a da Release que gerou o .zip), sem o "+commit" do final.
    public static string Current { get; } = Environment.GetEnvironmentVariable("MKV_VERSION") ?? // autoteste: fingir outra versão
        (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    private static string AppFolder => AppContext.BaseDirectory.TrimEnd('\\');

    // Pasta do app (não a de compilação), ou MKV_UPDATE_DIR no autoteste.
    public static bool Enabled => Environment.GetEnvironmentVariable("MKV_UPDATE_DIR") is not null ||
        !AppFolder.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase);

    private static HttpClient Client()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MKVisionDesktop/" + Current);
        return http;
    }

    // A versão mais nova publicada (com o .zip), se for mais nova que esta; null = está em dia.
    public static async Task<Release?> Check(CancellationToken token)
    {
        using var http = Client();
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases?per_page=20", token));
        Release? best = null;
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean()) continue;
            string version = release.GetProperty("tag_name").GetString()!.TrimStart('v', 'V');
            var zip = release.GetProperty("assets").EnumerateArray().FirstOrDefault(a => a.GetProperty("name").GetString()!.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (zip.ValueKind != JsonValueKind.Object || Compare(version, Current) <= 0) continue;
            if (best is not null && Compare(version, best.Version) <= 0) continue;
            best = new Release(version, zip.GetProperty("browser_download_url").GetString()!, zip.GetProperty("size").GetInt64(),
                release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "");
        }
        return best;
    }

    // Versões no formato 1.2.3 ou 1.2.3-beta.2: os números primeiro; com o mesmo número, a sem sufixo é a mais nova;
    // entre sufixos, parte por parte (números como números).
    public static int Compare(string a, string b)
    {
        static (int[] core, string[] pre) Parse(string v)
        {
            int dash = v.IndexOf('-');
            string core = dash < 0 ? v : v[..dash], pre = dash < 0 ? "" : v[(dash + 1)..];
            return ([.. core.Split('.').Select(p => int.TryParse(p, out int n) ? n : 0)], pre.Length == 0 ? [] : pre.Split('.'));
        }
        var (ca, pa) = Parse(a);
        var (cb, pb) = Parse(b);
        for (int i = 0; i < Math.Max(ca.Length, cb.Length); i++)
        {
            int x = i < ca.Length ? ca[i] : 0, y = i < cb.Length ? cb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        if (pa.Length == 0 || pb.Length == 0) return pb.Length.CompareTo(pa.Length); // sem sufixo vence
        for (int i = 0; i < Math.Min(pa.Length, pb.Length); i++)
        {
            bool na = int.TryParse(pa[i], out int x), nb = int.TryParse(pb[i], out int y);
            int c = na && nb ? x.CompareTo(y) : na ? -1 : nb ? 1 : string.CompareOrdinal(pa[i], pb[i]);
            if (c != 0) return c;
        }
        return pa.Length.CompareTo(pb.Length);
    }

    // Baixa o .zip e separa a pasta do app dele (numa pasta temporária). Devolve a pasta com o MKVisionDesktop.exe novo.
    public static async Task<string> Download(Release release, Action<long, long> progress, CancellationToken token)
    {
        string root = Path.Combine(Path.GetTempPath(), "MKVisionUpdate");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);
        string zip = Path.Combine(root, "app.zip"), folder = Path.Combine(root, "app");
        using (var http = Client())
        using (var response = await http.GetAsync(release.ZipUrl, HttpCompletionOption.ResponseHeadersRead, token))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? release.Size, done = 0, last = 0;
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using var output = File.Create(zip);
            var buffer = new byte[1 << 20];
            for (int n; (n = await input.ReadAsync(buffer, token)) > 0;)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), token);
                done += n;
                if (done - last > 4 << 20) { progress(done, total); last = done; }
            }
            progress(done, total);
        }
        ZipFile.ExtractToDirectory(zip, folder);
        File.Delete(zip);
        string exe = Directory.EnumerateFiles(folder, "MKVisionDesktop.exe", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new InvalidDataException("o .zip da versão nova não tem o MKVisionDesktop.exe");
        return Path.GetDirectoryName(exe)!;
    }

    // Com o app fechado (o script espera este processo sair), copia a pasta nova por cima da do app e abre de novo.
    public static void ApplyAfterExit(string newFolder)
    {
        string target = Environment.GetEnvironmentVariable("MKV_UPDATE_DIR") ?? AppFolder;
        string script = Path.Combine(Path.GetTempPath(), "MKVisionUpdate", "atualizar.cmd");
        int pid = Environment.ProcessId;
        bool restart = Environment.GetEnvironmentVariable("MKV_UPDATE_NORESTART") is null;
        File.WriteAllText(script, string.Join("\r\n",
            "@echo off",
            ":espera",
            $"tasklist /fi \"PID eq {pid}\" | find \"{pid}\" >nul && (timeout /t 1 /nobreak >nul & goto espera)",
            $"robocopy \"{newFolder}\" \"{target}\" /E /R:5 /W:2 /NFL /NDL /NJH /NJS /NP >nul",
            $"if errorlevel 8 (echo falhou > \"{Path.Combine(target, "atualizacao-falhou.txt")}\")",
            restart ? $"start \"\" \"{Path.Combine(target, "MKVisionDesktop.exe")}\"" : "rem (autoteste: sem abrir de novo)",
            $"rmdir /s /q \"{Path.GetDirectoryName(newFolder)}\"",
            ""), System.Text.Encoding.ASCII);
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        Log.Write($"Atualização: a versão nova vai para {target} quando o app fechar.");
    }

    // --updatetest: a comparação de versões e a consulta ao GitHub (com MKV_VERSION, fingindo uma versão antiga).
    public static int Test()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        int failures = 0;
        void Report(string what, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "OK    " : "FALHOU")} {what}: {detail}");
            if (!ok) failures++;
        }
        (string a, string b, int expected)[] cases =
        [
            ("1.0.0", "1.0.0-beta", 1), ("1.0.0-beta.2", "1.0.0-beta", 1), ("1.0.0-beta.10", "1.0.0-beta.2", 1), ("1.0.1-beta", "1.0.0", 1),
            ("1.0.0-beta", "1.0.0-beta", 0), ("1.0.0-alpha", "1.0.0-beta", -1), ("1.2", "1.10", -1)
        ];
        foreach (var (a, b, expected) in cases) Report($"{a} vs {b}", Math.Sign(Compare(a, b)) == expected, $"{Compare(a, b)}");
        Console.WriteLine($"esta versão: {Current}");
        try
        {
            var release = Check(CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine(release is null ? "GitHub: em dia (nenhuma mais nova)" : $"GitHub: {release.Version}, {release.Size / 1048576.0:0} MB — {release.ZipUrl}");
        }
        catch (Exception error) { Report("consulta ao GitHub", false, error.Message); }
        Console.WriteLine(failures == 0 ? "tudo certo" : $"{failures} falha(s)");
        return failures == 0 ? 0 : 1;
    }
}
