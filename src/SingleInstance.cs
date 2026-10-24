using System.Runtime.InteropServices;

namespace MKVision.Desktop;

// Uma janela só: a segunda cópia do MK Vision (ex.: dois cliques num vídeo no Explorer com o app já aberto)
// entrega o caminho do vídeo à janela do player que já existe (WM_COPYDATA) e fecha.
internal static class SingleInstance
{
    public const uint WM_COPYDATA = 0x004A;
    private const nint Tag = 0x4D4B56; // "MKV"

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT { public nint dwData; public int cbData; public IntPtr lpData; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? title);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, ref COPYDATASTRUCT data);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);

    // Segunda cópia: manda o vídeo (ou nada, só para trazer a janela para a frente).
    public static void Forward(string? path)
    {
        IntPtr window = IntPtr.Zero;
        for (int i = 0; i < 100 && window == IntPtr.Zero; i++) // a primeira cópia pode estar terminando de abrir
        {
            window = FindWindow("MKVisionPlayer", null);
            if (window == IntPtr.Zero) Thread.Sleep(100);
        }
        if (window == IntPtr.Zero) return;
        Native.GetWindowThreadProcessId(window, out uint processId);
        AllowSetForegroundWindow(processId); // quem foi aberto pelo clique pode passar o foco para a janela existente
        var text = Marshal.StringToHGlobalUni(path ?? "");
        try
        {
            var data = new COPYDATASTRUCT { dwData = Tag, cbData = ((path ?? "").Length + 1) * 2, lpData = text };
            SendMessage(window, WM_COPYDATA, IntPtr.Zero, ref data);
        }
        finally { Marshal.FreeHGlobal(text); }
    }

    // Janela do player: o caminho entregue ("" = só trazer para a frente); null = não é nosso.
    public static string? Receive(IntPtr lParam)
    {
        var data = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);
        if (data.dwData != Tag || data.lpData == IntPtr.Zero) return null;
        return Marshal.PtrToStringUni(data.lpData, Math.Max(0, data.cbData / 2 - 1));
    }
}
