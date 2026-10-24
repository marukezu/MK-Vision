// MK Vision Desktop — redimensiona uma textura com média de área (4x4 amostras por pixel de saída).
// Usado na câmera lenta: miniatura para detectar quadro real novo e cópia no tamanho da entrada da IA.

Texture2D<float4> Source : register(t0);
SamplerState LinearClamp : register(s0);

cbuffer Params : register(b0)
{
    float2 Footprint; // tamanho de um pixel de saída, em coordenadas da origem
    float2 Padding;
};

struct VSOut
{
    float4 Pos : SV_Position;
    float2 Uv : TEXCOORD0;
};

float4 PSResample(VSOut i) : SV_Target
{
    float3 sum = 0;
    [unroll] for (int y = 0; y < 4; y++)
    [unroll] for (int x = 0; x < 4; x++)
        sum += Source.SampleLevel(LinearClamp, i.Uv + (float2(x, y) - 1.5) / 4.0 * Footprint, 0).rgb;
    return float4(sum / 16.0, 1.0);
}
