using Vortice.Direct3D11;
using Vortice.DXGI;

namespace MKVision.Desktop;

// Textura que serve de entrada e saída de um passo (shader resource + render target), redimensionável.
internal sealed class RenderTexture(ID3D11Device device, Format format) : IDisposable
{
    public ID3D11Texture2D? Texture { get; private set; }
    public ID3D11ShaderResourceView? View { get; private set; }
    public ID3D11RenderTargetView? Target { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    // true se precisou recriar (conteúdo anterior perdido).
    public bool Ensure(int width, int height)
    {
        if (Texture is not null && width == Width && height == Height) return false;
        Dispose();
        Texture = device.CreateTexture2D(new Texture2DDescription(format, (uint)width, (uint)height, 1, 1,
            BindFlags.ShaderResource | BindFlags.RenderTarget));
        View = device.CreateShaderResourceView(Texture);
        Target = device.CreateRenderTargetView(Texture);
        (Width, Height) = (width, height);
        return true;
    }

    public void Dispose()
    {
        Target?.Dispose();
        View?.Dispose();
        Texture?.Dispose();
        Target = null;
        View = null;
        Texture = null;
    }
}
