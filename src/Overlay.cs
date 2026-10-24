using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;

namespace MKVision.Desktop;

// Janela sem borda, sempre no topo e "transparente" para o mouse, posicionada sobre o vídeo.
// Desenha via DirectComposition, o caminho que permite janela por cima sem capturar cliques.
// O Owl3D captura o monitor inteiro, então enxerga esta camada.
internal sealed class Overlay : IDisposable
{
    private const string ClassName = "MKVisionOverlay";
    private static readonly Native.WndProc WindowProc = Native.DefWindowProc; // referência fixa (o GC não pode coletar)

    public IntPtr Hwnd { get; }

    private readonly Gpu _gpu;
    private readonly IDCompositionDevice _composition;
    private readonly IDCompositionTarget _target;
    private readonly IDCompositionVisual _visual;
    private IDXGISwapChain1? _swapChain;
    private ID3D11RenderTargetView? _renderTarget;
    private int _width, _height;
    private Native.RECT _placement;
    private bool _visible;

    public Overlay(Gpu gpu)
    {
        _gpu = gpu;
        var windowClass = new Native.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WindowProc),
            hInstance = Native.GetModuleHandle(null),
            lpszClassName = ClassName
        };
        Native.RegisterClassEx(ref windowClass);
        Hwnd = Native.CreateWindowEx(
            Native.WS_EX_TOPMOST | Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE |
            Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOREDIRECTIONBITMAP,
            ClassName, "MK Vision", Native.WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, windowClass.hInstance, IntPtr.Zero);
        if (Hwnd == IntPtr.Zero) throw new InvalidOperationException($"CreateWindowEx falhou ({Marshal.GetLastWin32Error()})");

        using var dxgiDevice = gpu.Device.QueryInterface<IDXGIDevice>();
        _composition = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
        _composition.CreateTargetForHwnd(Hwnd, true, out _target).CheckError();
        _visual = _composition.CreateVisual();
        _target.SetRoot(_visual);
    }

    // Thread principal: segue a posição da janela do Edge (pixels físicos) e mostra/esconde.
    public void Place(Native.RECT rect, bool visible)
    {
        if (!visible || rect.Width <= 0 || rect.Height <= 0)
        {
            if (_visible) Native.ShowWindow(Hwnd, Native.SW_HIDE);
            _visible = false;
            return;
        }
        if (_visible && rect.Equals(_placement)) return;
        Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, rect.Left, rect.Top, rect.Width, rect.Height,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        _placement = rect;
        _visible = true;
    }

    // Thread da captura: devolve o alvo de desenho do tamanho pedido (cria/redimensiona a swap chain).
    public ID3D11RenderTargetView BeginFrame(int width, int height)
    {
        if (_swapChain is null)
        {
            using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
            _swapChain = factory.CreateSwapChainForComposition(_gpu.Device, new SwapChainDescription1
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = Format.B8G8R8A8_UNorm,
                BufferCount = 2,
                BufferUsage = Usage.RenderTargetOutput,
                SampleDescription = SampleDescription.Default,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = AlphaMode.Ignore,
                Scaling = Scaling.Stretch
            });
            _visual.SetContent(_swapChain);
            _composition.Commit();
            (_width, _height) = (width, height);
        }
        else if (width != _width || height != _height)
        {
            _renderTarget?.Dispose();
            _renderTarget = null;
            _swapChain.ResizeBuffers(2, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
            (_width, _height) = (width, height);
        }

        if (_renderTarget is null)
        {
            using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            _renderTarget = _gpu.Device.CreateRenderTargetView(backBuffer);
        }
        return _renderTarget;
    }

    public void Present() => _swapChain?.Present(1, PresentFlags.None);

    public void Dispose()
    {
        _renderTarget?.Dispose();
        _swapChain?.Dispose();
        _visual.Dispose();
        _target.Dispose();
        _composition.Dispose();
        Native.DestroyWindow(Hwnd);
    }
}
