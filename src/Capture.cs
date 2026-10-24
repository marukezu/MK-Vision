using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace MKVision.Desktop;

// Captura uma janela (o Edge) direto na GPU com Windows.Graphics.Capture.
// A captura de janela pega o conteúdo do próprio Edge, mesmo com a nossa camada por cima dele.
internal sealed class Capture : IDisposable
{
    private const DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid Texture2DIid = typeof(ID3D11Texture2D).GUID;

    private readonly IDirect3DDevice _device;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private SizeInt32 _size;

    // Chamado na thread da captura, a cada quadro novo da janela.
    // Último argumento: hora em que o Windows capturou o quadro (segundos no relógio do sistema, o mesmo do Stopwatch).
    public event Action<ID3D11Texture2D, SizeInt32, double>? FrameArrived;

    public Capture(Gpu gpu, IntPtr hwnd)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = GraphicsCaptureItemIid;
        IntPtr itemPointer = interop.CreateForWindow(hwnd, ref iid);
        var item = GraphicsCaptureItem.FromAbi(itemPointer);
        Marshal.Release(itemPointer);

        using (var dxgiDevice = gpu.Device.QueryInterface<IDXGIDevice>())
        {
            Marshal.ThrowExceptionForHR(Native.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr devicePointer));
            _device = MarshalInterface<IDirect3DDevice>.FromAbi(devicePointer);
            Marshal.Release(devicePointer);
        }

        _size = item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, PixelFormat, 2, _size);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = false; // o cursor real já aparece por cima de tudo
        TryRemoveYellowBorder();
        _session.StartCapture();
    }

    // O Windows desenha uma borda amarela em janelas capturadas; no Windows 11 dá para pedir para tirar.
    private void TryRemoveYellowBorder()
    {
        try
        {
            GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask().Wait(2000);
            _session.IsBorderRequired = false;
        }
        catch
        {
            // Windows sem suporte: a borda continua, o resto funciona igual.
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        using var frame = sender.TryGetNextFrame();
        if (frame is null) return;

        var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
        var iid = Texture2DIid;
        using var texture = new ID3D11Texture2D(access.GetInterface(ref iid));
        FrameArrived?.Invoke(texture, frame.ContentSize, frame.SystemRelativeTime.TotalSeconds);

        // Janela mudou de tamanho: recria o pool para os próximos quadros.
        if (frame.ContentSize.Width != _size.Width || frame.ContentSize.Height != _size.Height)
        {
            _size = frame.ContentSize;
            sender.Recreate(_device, PixelFormat, 2, _size);
        }
    }

    public void Dispose()
    {
        _pool.FrameArrived -= OnFrameArrived;
        _session.Dispose();
        _pool.Dispose();
    }
}
