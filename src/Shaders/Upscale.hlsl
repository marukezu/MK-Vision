// MK Vision Desktop — passos da fase 3 (resolução original → IA → tela).
//
// PSExtract:  recorta o vídeo da captura e reduz de volta à resolução original (média de área).
// PSTemporal: redução de ruído temporal — mistura com o quadro anterior só onde nada se mexeu.
// PSLuma:     luminância da região da lupa, entrada da IA.
// PSCompose:  amplia até a tela (bicúbica) e troca o brilho pelo da IA, mantendo a cor.

Texture2D<float4> Src : register(t0);
Texture2D<float4> Aux : register(t1);   // quadro anterior (temporal) ou saída da IA (compose)
SamplerState LinearClamp : register(s0);

cbuffer Params : register(b0)
{
    float4 SrcRect;     // região lida de Src, em coordenadas de textura: xy = início, zw = tamanho
    float2 SrcSize;     // tamanho de Src em pixels
    float2 Footprint;   // PSExtract: tamanho de um pixel de saída, em coordenadas de Src
    float4 AiRect;      // PSCompose: região da IA em coordenadas de Src
    float UseAi;
    float TemporalWeight;
    float TemporalThreshold;
    float Padding;
};

struct VSOut
{
    float4 Pos : SV_Position;
    float2 Uv : TEXCOORD0;
};

static const float3 LUMA = float3(0.2126, 0.7152, 0.0722);

// Cópia 1:1 (para o RTX Video, que só aceita 8 bits).
float4 PSCopy(VSOut i) : SV_Target
{
    return float4(Src.SampleLevel(LinearClamp, i.Uv, 0).rgb, 1.0);
}

float4 PSExtract(VSOut i) : SV_Target
{
    float2 center = SrcRect.xy + i.Uv * SrcRect.zw;
    float3 sum = 0;
    [unroll] for (int y = 0; y < 4; y++)
    [unroll] for (int x = 0; x < 4; x++)
        sum += Src.SampleLevel(LinearClamp, center + (float2(x, y) - 1.5) / 4.0 * Footprint, 0).rgb;
    return float4(sum / 16.0, 1.0);
}

float3 BoxMean(Texture2D<float4> t, float2 uv)
{
    float2 px = 1.0 / SrcSize;
    float3 sum = 0;
    [unroll] for (int y = -1; y <= 1; y++)
    [unroll] for (int x = -1; x <= 1; x++)
        sum += t.SampleLevel(LinearClamp, uv + float2(x, y) * px, 0).rgb;
    return sum / 9.0;
}

float4 PSTemporal(VSOut i) : SV_Target
{
    float3 current = Src.SampleLevel(LinearClamp, i.Uv, 0).rgb;
    float3 previous = Aux.SampleLevel(LinearClamp, i.Uv, 0).rgb;
    // Movimento medido na média 3x3 (o ruído some na média, o movimento não).
    float3 d = abs(BoxMean(Src, i.Uv) - BoxMean(Aux, i.Uv));
    float motion = max(d.r, max(d.g, d.b));
    float3 p = abs(current - previous);
    float pixel = max(p.r, max(p.g, p.b));
    float w = TemporalWeight * saturate(1.0 - motion / TemporalThreshold) * saturate(1.0 - pixel / (TemporalThreshold * 3.0));
    return float4(lerp(current, previous, w), 1.0);
}

float4 PSLuma(VSOut i) : SV_Target
{
    float y = dot(Src.SampleLevel(LinearClamp, SrcRect.xy + i.Uv * SrcRect.zw, 0).rgb, LUMA);
    return float4(y, 0, 0, 1);
}

// Catmull-Rom com 9 leituras bilineares.
float3 Bicubic(float2 uv)
{
    float2 pos = uv * SrcSize;
    float2 p1 = floor(pos - 0.5) + 0.5;
    float2 f = pos - p1;
    float2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
    float2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
    float2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
    float2 w3 = f * f * (-0.5 + 0.5 * f);
    float2 w12 = w1 + w2;
    float2 t0 = (p1 - 1.0) / SrcSize;
    float2 t3 = (p1 + 2.0) / SrcSize;
    float2 t12 = (p1 + w2 / w12) / SrcSize;
    float3 r = Src.SampleLevel(LinearClamp, float2(t0.x, t0.y), 0).rgb * w0.x * w0.y
             + Src.SampleLevel(LinearClamp, float2(t12.x, t0.y), 0).rgb * w12.x * w0.y
             + Src.SampleLevel(LinearClamp, float2(t3.x, t0.y), 0).rgb * w3.x * w0.y
             + Src.SampleLevel(LinearClamp, float2(t0.x, t12.y), 0).rgb * w0.x * w12.y
             + Src.SampleLevel(LinearClamp, float2(t12.x, t12.y), 0).rgb * w12.x * w12.y
             + Src.SampleLevel(LinearClamp, float2(t3.x, t12.y), 0).rgb * w3.x * w12.y
             + Src.SampleLevel(LinearClamp, float2(t0.x, t3.y), 0).rgb * w0.x * w3.y
             + Src.SampleLevel(LinearClamp, float2(t12.x, t3.y), 0).rgb * w12.x * w3.y
             + Src.SampleLevel(LinearClamp, float2(t3.x, t3.y), 0).rgb * w3.x * w3.y;
    return saturate(r);
}

float4 PSCompose(VSOut i) : SV_Target
{
    float2 uv = SrcRect.xy + i.Uv * SrcRect.zw;
    float3 color = Bicubic(uv);
    if (UseAi > 0.5)
    {
        float aiLuma = Aux.SampleLevel(LinearClamp, (uv - AiRect.xy) / AiRect.zw, 0).r;
        color = saturate(color + (aiLuma - dot(color, LUMA)));
    }
    return float4(color, 1.0);
}
