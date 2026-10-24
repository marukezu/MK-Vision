// MK Vision Desktop — câmera lenta: quadro intermediário entre os quadros reais A e B no momento S.
// A IA (RIFE) analisou o par em alguns momentos (Steps, uma camada de fluxo/máscara cada). Para S, usa as
// duas análises mais próximas — cada uma com o fluxo reescalado do seu momento para S — e mistura as duas
// pela distância (mesma lógica do shader da extensão).
//
// Duas versões:
//   PSInterpolate      — A e B são os quadros do vídeo (modo Edge);
//   PSInterpolateView  — A e B já foram processados (limpeza, IA de ampliação, nitidez) no tamanho e no
//                        recorte da tela (player): o pesado roda uma vez por quadro real, não a cada quadro de
//                        tela. Map leva o pixel da tela ao ponto do vídeo (lupa e giro inclusos) para ler o
//                        movimento; Delta leva o movimento (no vídeo) para a tela.

Texture2D<float4> FrameA : register(t0);
Texture2D<float4> FrameB : register(t1);
Texture2DArray<float4> Flow : register(t2); // por momento: xy = fluxo para A, zw = fluxo para B (pixels da análise)
Texture2DArray<float> Mask : register(t3);  // por momento: peso de A
SamplerState LinearClamp : register(s0);

cbuffer Params : register(b0)
{
    float S;
    int Count;      // 0 = sem análise: transição suave de A para B
    float2 FlowUv;  // parte útil da textura de fluxo (o modelo arredonda o tamanho para cima)
    float2 Analysis;
    float Crisp;    // 0–1: onde A e B discordam (oclusão, erro de movimento), puxa para o quadro real mais perto
    float Padding;
    float4 Steps[2]; // momentos analisados (até 8)
    float4 MapX;     // PSInterpolateView: u do vídeo = dot(MapX.xyz, (x, y, 1)) da tela
    float4 MapY;     //                    v do vídeo = dot(MapY.xyz, (x, y, 1))
    float4 Delta;    //                    deslocamento na tela = (Delta.xy · d, Delta.zw · d), d no vídeo (uv)
};

struct VSOut
{
    float4 Pos : SV_Position;
    float2 Uv : TEXCOORD0;
};

float StepAt(int i) { return Steps[i / 4][i % 4]; }

// Mistura de A e B. Onde as duas imagens concordam, a média de sempre; onde discordam muito (a mão cobriu ou
// descobriu algo, ou o movimento foi mal lido), a média vira um "fantasma" borrado — com Crisp, os pesos ficam
// mais decididos (potência), puxando para o quadro real mais próximo no tempo: nítido, com a troca rápida no meio.
float3 Combine(float3 a, float3 b, float wa, float wb)
{
    if (Crisp > 0)
    {
        float3 d = abs(a - b);
        float disagree = saturate((max(d.r, max(d.g, d.b)) - 0.04) / 0.10);
        float p = 1.0 + 7.0 * disagree * Crisp;
        float total = wa + wb + 1e-6;
        wa = pow(wa / total, p);
        wb = pow(wb / total, p);
    }
    return (a * wa + b * wb) / (wa + wb + 1e-6);
}

float3 WarpFrom(float2 uv, int layer, float t, float s)
{
    float3 f = float3(uv * FlowUv, layer);
    float4 flow = Flow.SampleLevel(LinearClamp, f, 0);
    float m = Mask.SampleLevel(LinearClamp, f, 0);
    float3 a = FrameA.SampleLevel(LinearClamp, uv + flow.xy * (s / t) / Analysis, 0).rgb;
    float3 b = FrameB.SampleLevel(LinearClamp, uv + flow.zw * ((1.0 - s) / (1.0 - t)) / Analysis, 0).rgb;
    float wa = m * (1.0 - s) / (1.0 - t);
    float wb = (1.0 - m) * s / t;
    return Combine(a, b, wa, wb);
}

float3 WarpView(float2 o, int layer, float t, float s)
{
    float2 src = float2(dot(MapX.xyz, float3(o, 1)), dot(MapY.xyz, float3(o, 1)));
    float3 f = float3(src * FlowUv, layer);
    float4 flow = Flow.SampleLevel(LinearClamp, f, 0);
    float m = Mask.SampleLevel(LinearClamp, f, 0);
    float2 da = flow.xy / Analysis, db = flow.zw / Analysis;
    float2 oa = o + float2(dot(Delta.xy, da), dot(Delta.zw, da)) * (s / t);
    float2 ob = o + float2(dot(Delta.xy, db), dot(Delta.zw, db)) * ((1.0 - s) / (1.0 - t));
    float3 a = FrameA.SampleLevel(LinearClamp, oa, 0).rgb;
    float3 b = FrameB.SampleLevel(LinearClamp, ob, 0).rgb;
    float wa = m * (1.0 - s) / (1.0 - t);
    float wb = (1.0 - m) * s / t;
    return Combine(a, b, wa, wb);
}

// Duas análises mais próximas de S (k e k+1) e o peso entre elas.
void Nearest(out int k, out float t0, out float t1, out float mix01)
{
    k = 0;
    [unroll] for (int j = 0; j < 7; j++)
        if (j + 1 < Count && S > StepAt(j + 1)) k = j + 1;
    t0 = StepAt(k);
    t1 = t0;
    mix01 = 0;
    if (k + 1 < Count && S > t0)
    {
        t1 = StepAt(k + 1);
        mix01 = (S - t0) / (t1 - t0);
    }
}

float4 PSInterpolate(VSOut i) : SV_Target
{
    // Sem análise pronta para o par: transição suave entre os dois quadros (melhor que segurar e pular).
    if (Count == 0) return float4(lerp(FrameA.SampleLevel(LinearClamp, i.Uv, 0).rgb, FrameB.SampleLevel(LinearClamp, i.Uv, 0).rgb, S), 1.0);
    int k; float t0, t1, w;
    Nearest(k, t0, t1, w);
    float3 color = WarpFrom(i.Uv, k, t0, S);
    if (w > 0) color = lerp(color, WarpFrom(i.Uv, k + 1, t1, S), w);
    return float4(color, 1.0);
}

float4 PSInterpolateView(VSOut i) : SV_Target
{
    if (Count == 0) return float4(lerp(FrameA.SampleLevel(LinearClamp, i.Uv, 0).rgb, FrameB.SampleLevel(LinearClamp, i.Uv, 0).rgb, S), 1.0);
    int k; float t0, t1, w;
    Nearest(k, t0, t1, w);
    float3 color = WarpView(i.Uv, k, t0, S);
    if (w > 0) color = lerp(color, WarpView(i.Uv, k + 1, t1, S), w);
    return float4(color, 1.0);
}
