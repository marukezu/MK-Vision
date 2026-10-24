using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MKVision.Desktop;

// Prioridades no Windows: de processador (classe do processo), de disco (prioridade de E/S) e de placa de vídeo (classe
// de agendamento da GPU, a que o Windows usa para dividir a placa entre os programas). O ComfyUI fica abaixo do normal
// nas três — gerando, ele usa tudo o que estiver livre, mas quando o Windows, o MK Vision ou o OWL3D pedem, eles passam
// na frente (é o "deixar uma folga": o Windows não tem um limite em % por programa). O MK Vision fica acima do normal.
internal static class Priorities
{
    [DllImport("ntdll.dll")] private static extern int NtSetInformationProcess(IntPtr process, int infoClass, ref int info, int length);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, out int info, int length, out int returned);
    [DllImport("gdi32.dll")] private static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr process, int priority);
    [DllImport("gdi32.dll")] private static extern int D3DKMTGetProcessSchedulingPriorityClass(IntPtr process, out int priority);

    private const int ProcessIoPriority = 33;            // classe de informação do processo
    public const int IoVeryLow = 0, IoLow = 1, IoNormal = 2;
    public const int GpuIdle = 0, GpuBelowNormal = 1, GpuNormal = 2, GpuAboveNormal = 3, GpuHigh = 4;

    // Abaixo do normal nas três. true = tudo aplicado.
    public static bool Lower(Process process)
    {
        bool ok = true;
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { ok = false; }
        int io = IoLow;
        ok &= NtSetInformationProcess(process.Handle, ProcessIoPriority, ref io, sizeof(int)) == 0;
        ok &= D3DKMTSetProcessSchedulingPriorityClass(process.Handle, GpuBelowNormal) == 0;
        return ok;
    }

    // O próprio app acima do normal (processador e placa).
    public static void RaiseSelf()
    {
        using var self = Process.GetCurrentProcess();
        try { self.PriorityClass = ProcessPriorityClass.AboveNormal; } catch (Exception error) { Log.Write($"Prioridade: processador — {error.Message}"); }
        int status = D3DKMTSetProcessSchedulingPriorityClass(self.Handle, GpuAboveNormal);
        if (status != 0) Log.Write($"Prioridade: placa de vídeo — 0x{status:X8}");
    }

    // O que o processo está usando agora: (processador, disco, placa).
    public static (ProcessPriorityClass cpu, int io, int gpu) Read(Process process)
    {
        var cpu = ProcessPriorityClass.Normal;
        try { cpu = process.PriorityClass; } catch { }
        if (NtQueryInformationProcess(process.Handle, ProcessIoPriority, out int io, sizeof(int), out _) != 0) io = -1;
        if (D3DKMTGetProcessSchedulingPriorityClass(process.Handle, out int gpu) != 0) gpu = -1;
        return (cpu, io, gpu);
    }

    public static string Text((ProcessPriorityClass cpu, int io, int gpu) p) =>
        $"processador {p.cpu}, disco {p.io switch { 0 => "muito baixa", 1 => "baixa", 2 => "normal", 3 => "alta", _ => "?" }}, " +
        $"placa {p.gpu switch { 0 => "ociosa", 1 => "abaixo do normal", 2 => "normal", 3 => "acima do normal", 4 => "alta", 5 => "tempo real", _ => "?" }}";
}
