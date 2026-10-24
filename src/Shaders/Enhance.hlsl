// MK Vision Desktop — mesmos filtros da extensão, em HLSL (Direct3D 11).
//
// PSClean:   remove blocos e faixas de compressão (média que ignora vizinhos muito diferentes).
// PSSharpen: micro-detalhe com AMD FidelityFX CAS na luminância + textura (contraste local) + limiar de ruído,
//            e depois cor e luz (clareza, brilho, contraste, saturação, vibração).
// PSDown:    reduz a imagem a 1/4 (média 4×4) — duas vezes dá o desfoque largo da "clareza".

Texture2D<float4> Src : register(t0);
Texture2D<float4> Wide : register(t1);  // imagem reduzida a 1/16 (desfoque largo, para a clareza)
Texture2D<float4> Whole : register(t2); // reduzida a ~1/256 (brilho médio da imagem, para o contraste)
SamplerState LinearClamp : register(s0);

cbuffer Params : register(b0)
{
    float2 Texel;        // 1 / tamanho da imagem
    float Detail;
    float DetailRadius;
    float TextureAmount;
    float TextureRadius;
    float Threshold;     // em 0–1
    float Deband;        // limiar da limpeza em 0–1 (0 = desligada)
    float Bypass;        // 1 = mostra a captura sem processar (comparação)
    float3 Padding;
    float Brightness;    // -0,5…0,5 (curva: preto e branco ficam no lugar)
    float Contrast;      // -0,5…0,5
    float Saturation;    // -1…1
    float Vibrance;      // 0…1 (aviva as cores fracas mais que as fortes; a pele quase não muda)
    float Clarity;       // 0…1 (contraste local largo: relevo e "vida" nos meios-tons)
    float3 Padding2;
};

struct VSOut
{
    float4 Pos : SV_Position;
    float2 Uv : TEXCOORD0;
};

VSOut VSMain(uint id : SV_VertexID)
{
    VSOut o;
    float2 p = float2((id << 1) & 2, id & 2);
    o.Uv = p;
    o.Pos = float4(p * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}

static const float3 LUMA = float3(0.2126, 0.7152, 0.0722);

float Hash(float2 p) { return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453); }

float4 PSClean(VSOut i) : SV_Target
{
    float3 c = Src.SampleLevel(LinearClamp, i.Uv, 0).rgb;
    float angle = Hash(i.Pos.xy) * 6.2831853; // giro aleatório evita padrões
    float3 sum = c;
    float weights = 1.0;
    // Anéis de 3, 7 e 12 px: alcance maior que um bloco de compressão (8 px) e que faixas típicas.
    const float radii[3] = { 3.0, 7.0, 12.0 };
    [unroll] for (int ring = 0; ring < 3; ring++)
    {
        [unroll] for (int k = 0; k < 6; k++)
        {
            float a = angle + k * 1.0471976 + ring * 0.5236;
            float3 s = Src.SampleLevel(LinearClamp, i.Uv + float2(cos(a), sin(a)) * radii[ring] * Texel, 0).rgb;
            float3 d = abs(s - c);
            float w = saturate(1.0 - max(d.r, max(d.g, d.b)) / Deband);
            w *= w;
            sum += s * w;
            weights += w;
        }
    }
    // Dither leve para o resultado de 8 bits não criar faixas novas.
    return float4(sum / weights + (Hash(i.Pos.xy + 0.37) - 0.5) / 255.0, 1.0);
}

float Luma(float2 uv) { return dot(Src.SampleLevel(LinearClamp, uv, 0).rgb, LUMA); }
float Core(float d) { return sign(d) * max(abs(d) - Threshold, 0.0); }

float4 PSSharpen(VSOut i) : SV_Target
{
    float2 uv = i.Uv;
    float3 e = Src.SampleLevel(LinearClamp, uv, 0).rgb;
    if (Bypass > 0.5) return float4(e, 1.0);

    float le = dot(e, LUMA);
    float2 s = Texel * DetailRadius;

    //  a b c
    //  d e f
    //  g h i
    float la = Luma(uv + float2(-s.x, -s.y)), lb = Luma(uv + float2(0, -s.y)), lc = Luma(uv + float2(s.x, -s.y));
    float ld = Luma(uv + float2(-s.x, 0)),                                     lf = Luma(uv + float2(s.x, 0));
    float lg = Luma(uv + float2(-s.x, s.y)),  lh = Luma(uv + float2(0, s.y)),  li = Luma(uv + float2(s.x, s.y));

    float mnCross = min(le, min(min(lb, ld), min(lf, lh)));
    float mxCross = max(le, max(max(lb, ld), max(lf, lh)));
    float mn = mnCross + min(mnCross, min(min(la, lc), min(lg, li)));
    float mx = mxCross + max(mxCross, max(max(la, lc), max(lg, li)));
    float amp = sqrt(saturate(min(mn, 2.0 - mx) / max(mx, 1e-4)));
    float w = -amp / 6.5;
    float cas = (w * (lb + ld + lf + lh) + le) / (1.0 + 4.0 * w);
    float delta = Detail * Core(cas - le);

    if (TextureAmount > 0.0)
    {
        float sum = le;
        float2 t = Texel * TextureRadius;
        [unroll] for (int k = 0; k < 8; k++)
        {
            float angle = k * 0.7853982;
            float2 o = float2(cos(angle), sin(angle)) * t;
            sum += Luma(uv + o) + Luma(uv + o * 0.5);
        }
        delta += TextureAmount * Core(le - sum / 17.0);
    }

    float3 c = saturate(e + delta);

    // Clareza: realça a diferença para o entorno largo, só nos meios-tons (não esmaga preto nem estoura branco).
    if (Clarity > 0.0)
    {
        float l = dot(c, LUMA);
        float wide = dot(Wide.SampleLevel(LinearClamp, uv, 0).rgb, LUMA);
        float mid = 1.0 - (2.0 * l - 1.0) * (2.0 * l - 1.0);
        c = saturate(c + Clarity * 0.9 * (l - wide) * mid);
    }
    // Brilho como curva (gama): clareia ou escurece os meios-tons; preto e branco não mudam.
    if (Brightness != 0.0) c = pow(max(c, 0.0), exp(-Brightness * 1.4));
    // Contraste em volta do brilho médio da imagem (numa cena escura não esmaga as sombras): curva que afasta
    // (ou aproxima) do meio e nunca corta preto nem branco.
    if (Contrast != 0.0)
    {
        float pivot = 0.25 * dot(Whole.SampleLevel(LinearClamp, float2(0.25, 0.25), 0).rgb
                               + Whole.SampleLevel(LinearClamp, float2(0.75, 0.25), 0).rgb
                               + Whole.SampleLevel(LinearClamp, float2(0.25, 0.75), 0).rgb
                               + Whole.SampleLevel(LinearClamp, float2(0.75, 0.75), 0).rgb, LUMA);
        pivot = clamp(pivot, 0.15, 0.7);
        float g = exp(Contrast * 1.6); // >1 mais contraste, <1 menos
        float3 low = pivot * pow(saturate(c / pivot), g);
        float3 high = 1.0 - (1.0 - pivot) * pow(saturate((1.0 - c) / (1.0 - pivot)), g);
        c = c < pivot ? low : high;
    }
    // Saturação e vibração.
    if (Saturation != 0.0 || Vibrance > 0.0)
    {
        float l = dot(c, LUMA);
        float chroma = max(c.r, max(c.g, c.b)) - min(c.r, min(c.g, c.b));
        // Pele (vermelho > verde > azul): a vibração quase não mexe nela.
        float skin = saturate((c.r - c.g) * 4.0) * saturate((c.g - c.b) * 4.0) * saturate(1.0 - abs(c.r - c.g - (c.g - c.b)) * 3.0);
        float boost = 1.0 + Saturation + Vibrance * 1.2 * (1.0 - chroma) * (1.0 - 0.7 * skin);
        c = saturate(lerp(l.xxx, c, boost));
    }
    return float4(c, 1.0);
}

// Média 4×4: quatro leituras bilineares (cada uma já é a média de 2×2) em volta do centro do bloco.
float4 PSDown(VSOut i) : SV_Target
{
    float3 sum = Src.SampleLevel(LinearClamp, i.Uv + float2(-1, -1) * Texel, 0).rgb
               + Src.SampleLevel(LinearClamp, i.Uv + float2( 1, -1) * Texel, 0).rgb
               + Src.SampleLevel(LinearClamp, i.Uv + float2(-1,  1) * Texel, 0).rgb
               + Src.SampleLevel(LinearClamp, i.Uv + float2( 1,  1) * Texel, 0).rgb;
    return float4(sum * 0.25, 1.0);
}
