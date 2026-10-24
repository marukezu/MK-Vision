// MK Vision — giro do player (Insert/Delete, passos de 45°) e espelho (Home: horizontal, End: vertical, na tela):
// desenha o quadro girado dentro da "caixa" que o
// contém (90° = largura e altura trocadas; 45° = um quadrado maior com cantos pretos). Feito no quadro original,
// antes da limpeza, da IA e da nitidez; amostragem bicúbica (Catmull-Rom, 9 leituras) para não amolecer a
// imagem, e borda com meia transição para a diagonal não ficar serrilhada.

Texture2D<float4> Source : register(t0);
SamplerState LinearClamp : register(s0);

cbuffer Params : register(b0)
{
    float2 SrcSize; // quadro original (px)
    float2 BoxSize; // caixa girada (px)
    float Cos;
    float Sin;
    float2 Flip; // 1 ou -1 por eixo: espelha a imagem da tela (antes de girar de volta para o quadro)
};

struct VSOut
{
    float4 Pos : SV_Position;
    float2 Uv : TEXCOORD0;
};

float3 CatmullRom(float2 uv)
{
    float2 position = uv * SrcSize;
    float2 p1 = floor(position - 0.5) + 0.5;
    float2 f = position - p1;
    float2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
    float2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
    float2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
    float2 w3 = f * f * (-0.5 + 0.5 * f);
    float2 w12 = w1 + w2;
    float2 p0 = (p1 - 1.0) / SrcSize;
    float2 p3 = (p1 + 2.0) / SrcSize;
    float2 p12 = (p1 + w2 / w12) / SrcSize;
    float3 c = 0;
    c += Source.SampleLevel(LinearClamp, float2(p0.x, p0.y), 0).rgb * w0.x * w0.y;
    c += Source.SampleLevel(LinearClamp, float2(p12.x, p0.y), 0).rgb * w12.x * w0.y;
    c += Source.SampleLevel(LinearClamp, float2(p3.x, p0.y), 0).rgb * w3.x * w0.y;
    c += Source.SampleLevel(LinearClamp, float2(p0.x, p12.y), 0).rgb * w0.x * w12.y;
    c += Source.SampleLevel(LinearClamp, float2(p12.x, p12.y), 0).rgb * w12.x * w12.y;
    c += Source.SampleLevel(LinearClamp, float2(p3.x, p12.y), 0).rgb * w3.x * w12.y;
    c += Source.SampleLevel(LinearClamp, float2(p0.x, p3.y), 0).rgb * w0.x * w3.y;
    c += Source.SampleLevel(LinearClamp, float2(p12.x, p3.y), 0).rgb * w12.x * w3.y;
    c += Source.SampleLevel(LinearClamp, float2(p3.x, p3.y), 0).rgb * w3.x * w3.y;
    return max(c, 0);
}

float4 PSRotate(VSOut i) : SV_Target
{
    // Pixel da caixa (a partir do centro) → pixel do quadro original: gira de volta (sentido horário na tela).
    float2 p = (i.Uv - 0.5) * BoxSize * Flip;
    float2 q = float2(Cos * p.x + Sin * p.y, -Sin * p.x + Cos * p.y);
    float2 uv = q / SrcSize + 0.5;
    float2 inside = min(uv, 1.0 - uv) * SrcSize; // distância até a borda do quadro (px)
    float coverage = saturate(min(inside.x, inside.y) + 0.5);
    if (coverage <= 0) return float4(0, 0, 0, 1);
    return float4(CatmullRom(saturate(uv)) * coverage, 1);
}
