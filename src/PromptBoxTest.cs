using System.Runtime.InteropServices;

namespace MKVision.Desktop;

// --promptboxtest <imagem>: a janela do G com as abas do ComfyUI — as abas cabem na janela sem cortar o nome; a aba com a
// imagem 2 troca o "Gerar do zero" pelo botão; "Gerar" sem a imagem 2 avisa e não fecha; o botão escolhe o arquivo (no
// teste, sem a janela do Windows) e o "Gerar" devolve a aba e o arquivo.
internal static class PromptBoxTest
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowW(string? className, string? title);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr dialog, int id);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, char[] text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Native.RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeoutW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool GetTextExtentPoint32W(IntPtr dc, string text, int length, out Native.POINT size);

    private const uint BM_CLICK = 0x00F5, WM_GETFONT = 0x0031;
    private const int IdOk = 1, IdTab = 150, IdSecond = 176;

    public static int Run(string image)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        int failures = 0;
        void Check(string what, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "OK    " : "FALHOU")} {what}: {detail}");
            if (!ok) failures++;
        }
        var owner = Native.CreateWindowEx(0, "STATIC", "dono", 0x80000000 /* WS_POPUP */ | 0x10000000 /* WS_VISIBLE */, 100, 100, 900, 700,
            IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);
        var tabs = ComfyClient.Workflows.Select(wf => new PromptBox.Tab
        {
            Label = wf.Tab, CanScratch = wf.Scratch, SecondImage = wf.SecondImage, Prompt = "teste",
            Sampler = new ComfyClient.SamplerSetting(30, 3, 1024, 1, false, 0)
        }).ToList();
        int second = tabs.FindIndex(t => t.SecondImage);
        string Text(IntPtr hwnd)
        {
            var buffer = new char[512];
            return new string(buffer, 0, GetWindowTextW(hwnd, buffer, buffer.Length));
        }

        // Quem mexe na janela: outra thread (a do teste fica no laço da janela, como no player).
        var driver = new Thread(() =>
        {
            IntPtr box = IntPtr.Zero;
            for (int i = 0; i < 100 && box == IntPtr.Zero; i++, Thread.Sleep(50)) box = FindWindowW("MKVisionPromptBox", null);
            if (box == IntPtr.Zero)
            {
                Check("a janela do G abre", false, "não achei");
                return;
            }
            Thread.Sleep(300);
            // As abas: lado a lado, dentro da janela, cada uma com o nome inteiro.
            GetClientRect(box, out var client);
            var font = SendMessageW(GetDlgItem(box, IdTab), WM_GETFONT, IntPtr.Zero, IntPtr.Zero);
            var dc = GetDC(box);
            var old = SelectObject(dc, font);
            var problems = new List<string>();
            int lastRight = 0;
            for (int i = 0; i < tabs.Count; i++)
            {
                var button = GetDlgItem(box, IdTab + i);
                PlayerNative.GetWindowRect(button, out var r);
                GetTextExtentPoint32W(dc, tabs[i].Label, tabs[i].Label.Length, out var size);
                if (r.Width < size.X + 6) problems.Add($"\"{tabs[i].Label}\" cortada ({r.Width} < {size.X})");
                if (i > 0 && r.Left < lastRight) problems.Add($"\"{tabs[i].Label}\" por cima da anterior");
                lastRight = r.Right;
            }
            SelectObject(dc, old);
            ReleaseDC(box, dc);
            PlayerNative.GetWindowRect(GetDlgItem(box, IdTab + tabs.Count - 1), out var lastTab);
            var corner = new Native.POINT { X = client.Right, Y = 0 };
            Native.ClientToScreen(box, ref corner);
            if (lastTab.Right > corner.X) problems.Add("a última passa da janela");
            Check($"{tabs.Count} abas: cabem, sem cortar o nome", problems.Count == 0, problems.Count == 0 ? "ok" : string.Join("; ", problems));

            var scratch = GetDlgItem(box, 102);
            var chooser = GetDlgItem(box, IdSecond);
            Check("aba normal: \"Gerar do zero\", sem o botão da imagem 2", IsWindowVisible(scratch) && !IsWindowVisible(chooser), $"do zero {IsWindowVisible(scratch)}, botão {IsWindowVisible(chooser)}");
            SendMessageTimeoutW(GetDlgItem(box, IdTab + second), BM_CLICK, IntPtr.Zero, IntPtr.Zero, 0, 2000, out _);
            Thread.Sleep(200);
            Check("aba das 2 imagens: o botão no lugar do \"do zero\"", !IsWindowVisible(scratch) && IsWindowVisible(chooser), $"do zero {IsWindowVisible(scratch)}, botão {IsWindowVisible(chooser)}");
            // O rótulo do arquivo é o próximo controle depois do botão.
            var label = GetWindow(chooser, 2 /* GW_HWNDNEXT */);
            SendMessageTimeoutW(GetDlgItem(box, IdOk), BM_CLICK, IntPtr.Zero, IntPtr.Zero, 0, 2000, out _);
            Thread.Sleep(300);
            Check("Gerar sem a imagem 2: avisa e não fecha", FindWindowW("MKVisionPromptBox", null) == box && Text(label).Contains("Escolha a imagem 2"), $"\"{Text(label)}\"");
            PromptBox.TestSecondPath = image;
            SendMessageTimeoutW(chooser, BM_CLICK, IntPtr.Zero, IntPtr.Zero, 0, 2000, out _);
            Thread.Sleep(200);
            Check("o botão escolhe a imagem 2", Text(label).Contains(Path.GetFileName(image)), $"\"{Text(label)}\"");
            SendMessageTimeoutW(GetDlgItem(box, IdOk), BM_CLICK, IntPtr.Zero, IntPtr.Zero, 0, 2000, out _);
        }) { IsBackground = true };
        driver.Start();
        var answer = PromptBox.Show(owner, "ComfyUI (tecla G)", tabs, 0);
        PromptBox.TestSecondPath = null;
        Check("Gerar: a aba das 2 imagens e o arquivo", answer is { } a && a.tab == second && a.second == image, answer is { } b ? $"aba {b.tab}, \"{b.second}\"" : "cancelado");
        Native.DestroyWindow(owner);
        Console.WriteLine(failures == 0 ? "tudo certo" : $"{failures} falha(s)");
        return failures == 0 ? 0 : 1;
    }
}
