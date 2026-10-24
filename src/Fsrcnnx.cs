using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace MKVision.Desktop;

// Rede neural FSRCNNX (upscaling 2x da luminância): lê os "user shaders" do mpv (Models/*.glsl),
// converte cada passo para HLSL e compila em segundo plano, guardando o resultado em cache.
internal sealed partial class Fsrcnnx : IDisposable
{
    private sealed record Pass(string[] Binds, string? Save, int Scale, string Hlsl)
    {
        public ID3D11PixelShader? Shader;
    }

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MKVisionDesktop", "shadercache");

    private readonly Gpu _gpu;
    private readonly Pass[] _passes;
    private volatile bool _ready;
    public bool Ready => _ready;
    public string Name { get; }

    public Fsrcnnx(Gpu gpu, string file)
    {
        _gpu = gpu;
        Name = Path.GetFileNameWithoutExtension(file);
        _passes = Parse(File.ReadAllText(file));
        Task.Run(Compile); // compila sem travar a captura; até lá o pipeline usa só a bicúbica
    }

    private void Compile()
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var started = DateTime.Now;
            foreach (var pass in _passes)
            {
                string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pass.Hlsl)))[..24];
                string cached = Path.Combine(CacheDir, hash + ".cso");
                byte[] code;
                if (File.Exists(cached))
                {
                    code = File.ReadAllBytes(cached);
                }
                else
                {
                    var result = Compiler.Compile(pass.Hlsl, "PSMain", Name, "ps_5_0", out Blob blob, out Blob? errors);
                    if (result.Failure) throw new InvalidOperationException(errors?.AsString());
                    code = blob.AsBytes();
                    File.WriteAllBytes(cached, code);
                }
                pass.Shader = _gpu.Device.CreatePixelShader(code);
            }
            _ready = true;
            Log.Write($"IA {Name}: pronta ({(DateTime.Now - started).TotalSeconds:0.0} s)");
        }
        catch (Exception error)
        {
            Log.Write($"IA {Name}: falhou ao compilar — {error.Message}");
        }
    }

    // ---------- Conversão GLSL (mpv) → HLSL ----------

    private static Pass[] Parse(string text)
    {
        var passes = new List<Pass>();
        foreach (string block in HookSplit().Split(text))
        {
            if (!block.StartsWith("//!HOOK")) continue;
            var lines = block.Split('\n');
            var binds = new List<string>();
            string? save = null;
            int scale = 1, i = 0;
            for (; i < lines.Length && lines[i].StartsWith("//!"); i++)
            {
                var parts = lines[i][3..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts[0] == "BIND") binds.Add(parts[1]);
                else if (parts[0] == "SAVE") save = parts[1];
                else if (parts[0] == "WIDTH") scale = int.Parse(parts[2]); // "LUMA.w 2 *"
            }
            string body = ToHlsl(string.Join('\n', lines[i..]));
            passes.Add(new Pass([.. binds], save, scale, Wrap(binds, body)));
        }
        return [.. passes];
    }

    private static string ToHlsl(string glsl)
    {
        string s = glsl;
        s = Regex.Replace(s, @"\bivec2\b", "int2");
        s = Regex.Replace(s, @"\bvec2\b", "float2");
        s = Regex.Replace(s, @"\bvec4\b", "float4");
        s = Regex.Replace(s, @"\bfract\(", "frac(");
        s = Regex.Replace(s, @"_texOff\(0\)", "_texOff(float2(0, 0))");
        // float(X_texOff(...)) → primeira componente
        s = Regex.Replace(s, @"float\((\w+_texOff\(float2\([^()]*\)\))\)", "($1).x");
        // mat4(a0..a15) * v (colunas em GLSL) → mul(v, float4x4(a0..a15)) (linhas em HLSL): mesmo resultado
        s = Regex.Replace(s, @"\bmat4(?:x4)?\(([^()]*)\)\s*\*\s*(\w+_texOff\(float2\([^()]*\)\))", "mul($2, float4x4($1))");
        // Construtor de um argumento só (vec4(0.0)) não existe em HLSL: vira conversão.
        s = Regex.Replace(s, @"\b(float2|float4|int2)\(([^,()]+)\)", "(($1)($2))");
        if (Regex.IsMatch(s, @"\b(mat[234]|vec3|mix)\b")) throw new NotSupportedException("construção GLSL não convertida");
        return s;
    }

    private static string Wrap(IEnumerable<string> binds, string body)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SamplerState PointClamp : register(s0);");
        sb.AppendLine("cbuffer P : register(b0) { float2 uSize; float2 uPad; };");
        sb.AppendLine("static float2 vPos;");
        int slot = 0;
        foreach (string name in binds)
        {
            sb.AppendLine($"Texture2D<float4> {name}_raw : register(t{slot++});");
            sb.AppendLine($"#define {name}_pos vPos");
            sb.AppendLine($"#define {name}_size uSize");
            sb.AppendLine($"#define {name}_pt (1.0 / uSize)");
            sb.AppendLine($"#define {name}_tex(p) {name}_raw.SampleLevel(PointClamp, p, 0)");
            sb.AppendLine($"float4 {name}_texOff(float2 o) {{ return {name}_raw.SampleLevel(PointClamp, vPos + o / uSize, 0); }}");
        }
        sb.AppendLine(body);
        sb.AppendLine("struct VSOut { float4 Pos : SV_Position; float2 Uv : TEXCOORD0; };");
        sb.AppendLine("float4 PSMain(VSOut i) : SV_Target { vPos = i.Uv; return hook(); }");
        return sb.ToString();
    }

    [GeneratedRegex(@"^(?=//!HOOK)", RegexOptions.Multiline)]
    private static partial Regex HookSplit();

    // ---------- Execução ----------

    // Texturas intermediárias de uma execução (cada tamanho de entrada tem a sua).
    public sealed class Runner(Gpu gpu) : IDisposable
    {
        private readonly Dictionary<string, RenderTexture> _textures = [];
        private int _width, _height;

        public RenderTexture Get(string name, int width, int height)
        {
            if (!_textures.TryGetValue(name, out var texture))
                _textures[name] = texture = new RenderTexture(gpu.Device, Format.R16G16B16A16_Float);
            texture.Ensure(width, height);
            return texture;
        }

        public void Resize(int width, int height) => (_width, _height) = (width, height);
        public int Width => _width;
        public int Height => _height;

        public void Dispose()
        {
            foreach (var texture in _textures.Values) texture.Dispose();
            _textures.Clear();
        }
    }

    // Roda a rede: entrada "LUMA" (luminância em .x, tamanho w×h) → saída 2w×2h (luminância em .x).
    public ID3D11ShaderResourceView Run(Runner runner, ID3D11ShaderResourceView luma, int width, int height)
    {
        var context = _gpu.Context;
        _gpu.SetSize(width, height);
        RenderTexture? output = null;
        foreach (var pass in _passes)
        {
            output = runner.Get(pass.Save ?? "OUTPUT", width * pass.Scale, height * pass.Scale);
            context.OMSetRenderTargets(output.Target!);
            context.RSSetViewport(0, 0, output.Width, output.Height);
            context.PSSetShader(pass.Shader!);
            for (int slot = 0; slot < pass.Binds.Length; slot++)
            {
                string name = pass.Binds[slot];
                context.PSSetShaderResource((uint)slot, name == "LUMA" ? luma : runner.Get(name, width, height).View!);
            }
            context.Draw(3, 0);
            for (int slot = 0; slot < pass.Binds.Length; slot++) context.PSUnsetShaderResource((uint)slot);
        }
        context.OMSetRenderTargets((ID3D11RenderTargetView?)null);
        return output!.View!;
    }

    public void Dispose()
    {
        foreach (var pass in _passes) pass.Shader?.Dispose();
    }
}
