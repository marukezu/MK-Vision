// MK Vision — interface do player (barra de controles, menu) por cima do vídeo: só copia a camada, que já vem
// com alfa pré-multiplicado; a mistura com o vídeo é feita pelo estado de blend (One, InvSrcAlpha).

Texture2D<float4> Layer : register(t0);
SamplerState PointClamp : register(s0);

struct VSOut
{
    float4 Pos : SV_Position;
    float2 Uv : TEXCOORD0;
};

float4 PSOverlay(VSOut i) : SV_Target
{
    return Layer.SampleLevel(PointClamp, i.Uv, 0);
}
