using System.Text.Json;

namespace MKVision.Desktop;

// Perfis de nitidez (os mesmos valores dos perfis da extensão, mas o player é independente dela).
internal sealed record PlayerProfile(string Name, float Detail, float DetailRadius, float Texture, float TextureRadius, float Threshold, float Deband)
{
    public static readonly PlayerProfile[] All =
    [
        new("Padrão", 1.0f, 0.8f, 0.35f, 3f, 2f, 2f),
        new("Pele / Detalhe", 1.6f, 0.7f, 0.5f, 3f, 2f, 2f),
        new("Vídeo ruim", 0.9f, 1.4f, 0.6f, 5f, 4f, 5f),
        new("Suave", 0.6f, 0.8f, 0.2f, 3f, 1f, 3f)
    ];
}

// Ajustes do player: nitidez (perfil e controles finos), força, potência da IA e câmera lenta. Não têm nada a
// ver com a extensão; ficam guardados em %LOCALAPPDATA%\MKVisionDesktop\player.json e o player abre igual.
internal sealed class PlayerSettings
{
    // Campos com padrão: um arquivo de uma versão anterior (sem algum campo) continua valendo.
    private sealed class Stored
    {
        public float Detail { get; set; } = 1.6f;
        public float DetailRadius { get; set; } = 0.7f;
        public float Texture { get; set; } = 0.5f;
        public float TextureRadius { get; set; } = 3f;
        public float Threshold { get; set; } = 2f;
        public float Deband { get; set; } = 2f;
        public float Strength { get; set; } = 1f;
        public int Power { get; set; } = 3;
        public bool Smooth { get; set; } = true;
        public float Volume { get; set; } = 1f;
        public bool Muted { get; set; }
        public int ListMode { get; set; } = 2;
        public int Performance { get; set; } = 1;
        public bool Ensemble { get; set; }
        public bool Crisp { get; set; }
        public bool TrainedModel { get; set; } = true;
        public string WebUrl { get; set; } = "";
        public bool WebAuto { get; set; } = true;
        public List<string>? WebTabs { get; set; }
        public int WebTab { get; set; }
        public bool WebBookmarksBar { get; set; } = true;
        public int PanelTab { get; set; }
        public bool RtxVideo { get; set; } = true;
        public int PhotoAi { get; set; } = 1;
        public float PhotoTexture { get; set; } = 0.4f;
        public float PhotoGrain { get; set; }
        public bool PhotoSkin { get; set; } = true;
        public bool PhotoFullRes { get; set; }
        public bool PhotoFaceClean { get; set; } = true;
        public float ToneExposure { get; set; } = 0.7f;
        public float Brightness { get; set; }
        public float Contrast { get; set; }
        public float Saturation { get; set; }
        public float Vibrance { get; set; }
        public float Clarity { get; set; }
        public int WebStartup { get; set; }
        public List<string>? WebStartupPages { get; set; }
        public List<string>? WebMobileSites { get; set; }
        public List<string>? AdBlockAllowed { get; set; }
        public bool WebClearOnClose { get; set; } = true;
        public bool WebSmartScreen { get; set; } = true;
        public List<string>? AdBlockApplied { get; set; }
        public int WebSleepMinutes { get; set; } = 30;
        public Dictionary<string, double>? WebZoom { get; set; }
        public int WebColorScheme { get; set; }
        public bool WebForceDark { get; set; }
        public string? LastSaveFolder { get; set; }
    }

    private static readonly string FilePath = Environment.GetEnvironmentVariable("MKV_SETTINGS") is { Length: > 0 } test ? test // autoteste
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MKVisionDesktop", "player.json");

    // O que a limpeza, a IA e a nitidez leem (a limpeza segue o controle "remover artefatos", não a potência).
    public Settings Filter { get; } = new() { ManualDeband = true, RtxVideo = true };
    public bool Smooth = true;
    public bool Ensemble;     // câmera lenta: IA de quadros em "conjunto" (duas leituras combinadas; tecla E)
    public bool Crisp;        // câmera lenta "nítida": sem fantasma onde os quadros discordam (tecla N)
    public bool TrainedModel = true; // câmera lenta: IA treinada com os vídeos do usuário, se existir (tecla M)
    public string WebUrl = "";  // navegador: última página
    public bool WebAuto = true; // navegador: vídeo que começa a tocar (com som) abre no player
    public List<string> WebTabs = []; // navegador: abas abertas (voltam na próxima vez) e a ativa
    public int WebTab;
    public bool WebBookmarksBar = true;
    public int PanelTab; // painel de ajustes: aba aberta (0 imagem, 1 câmera lenta, 2 zoom, 3 cor e luz, 4 fotos)
    public int PhotoAi = 1; // IA de fotos: 0 desligada, 1 Real-ESRGAN, 2 UltraSharp, 3 a máxima, 4 rosto, 5 rosto nítido
    public int WebStartup; // navegador ao abrir: 0 continua de onde parou, 1 só a página inicial, 2 as páginas abaixo
    public List<string> WebStartupPages = [];
    public List<string> WebMobileSites = ["instagram.com"]; // navegador: sites abertos como num celular (Android)
    // Sites sem bloqueio de anúncios. O Instagram vem livre: a lista de privacidade bloqueia os registros do que você
    // assiste (instagram.com/logging, /ajax/bz) e o algoritmo não aprendia nada do que você vê pelo navegador.
    public List<string> AdBlockAllowed = ["instagram.com"];
    public List<string> AdBlockApplied = []; // os que já foram liberados no uBlock (para devolver os removidos)
    public bool WebClearOnClose = true; // navegador: apaga o histórico interno e o cache ao fechar
    public bool WebSmartScreen = true;  // navegador: aviso de site perigoso da Microsoft (manda os endereços para ela)
    public int WebSleepMinutes = 30;    // navegador: aba parada há tanto tempo "dorme" (libera memória e placa); 0 = nunca
    public Dictionary<string, double> WebZoom = new(StringComparer.OrdinalIgnoreCase); // navegador: zoom de cada site (sem os de 100%)
    public int WebColorScheme;          // navegador: tema que os sites veem — 0 o do Windows, 1 escuro, 2 claro
    public bool WebForceDark;           // navegador: escurece também os sites que não têm tema escuro
    public string LastSaveFolder = "";  // Ctrl+S / Shift+S: a última pasta onde você salvou (o "Salvar como" abre nela)
    public float Volume = 1f; // 0–1
    public bool Muted;
    public int ListMode = 2; // lista da pasta: 1 ícones extra grandes, 2 grandes, 3 médios, 4 só os nomes

    // Desempenho: 0 qualidade máxima, 1 equilibrado, 2 leve (para usar com VR) — quanto da placa a IA de quadros
    // pode usar e se a IA de ampliação é a rápida.
    public static readonly string[] PerformanceNames = ["Qualidade máxima", "Equilibrado", "Leve (para usar com VR)"];
    private int _performance = 1;
    public int Performance
    {
        get => _performance;
        set
        {
            _performance = Math.Clamp(value, 0, 2);
            Filter.LightAi = _performance == 2;
        }
    }
    // Parte do tempo de cada par que a IA de quadros pode usar, e quantas análises no máximo.
    public (double budget, int maxSteps) SlowBudget => Performance switch { 0 => (0.7, 8), 1 => (0.35, 4), _ => (0.2, 2) };

    public static PlayerSettings Load()
    {
        var settings = new PlayerSettings();
        settings.Apply(PlayerProfile.All[1]); // começa em "Pele / Detalhe"
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath)) is { } s)
            {
                var f = settings.Filter;
                (f.Detail, f.DetailRadius, f.Texture, f.TextureRadius, f.Threshold, f.Deband) =
                    (s.Detail, s.DetailRadius, s.Texture, s.TextureRadius, s.Threshold, s.Deband);
                f.Strength = Math.Clamp(s.Strength, 0, 4);
                f.Power = Math.Clamp(s.Power, 1, 5);
                f.RtxVideo = s.RtxVideo;
                (f.Brightness, f.Contrast, f.Saturation) = (Math.Clamp(s.Brightness, -0.5f, 0.5f), Math.Clamp(s.Contrast, -0.5f, 0.5f), Math.Clamp(s.Saturation, -1f, 1f));
                (f.Vibrance, f.Clarity) = (Math.Clamp(s.Vibrance, 0f, 1f), Math.Clamp(s.Clarity, 0f, 1f));
                settings.Smooth = s.Smooth;
                settings.Volume = Math.Clamp(s.Volume, 0, 1);
                settings.Muted = s.Muted;
                settings.ListMode = Math.Clamp(s.ListMode, 1, 4);
                settings.Performance = s.Performance;
                settings.Ensemble = s.Ensemble;
                settings.Crisp = s.Crisp;
                settings.TrainedModel = s.TrainedModel;
                settings.WebUrl = s.WebUrl ?? "";
                settings.WebAuto = s.WebAuto;
                settings.WebTabs = s.WebTabs ?? [];
                settings.WebTab = s.WebTab;
                settings.WebBookmarksBar = s.WebBookmarksBar;
                settings.PanelTab = Math.Clamp(s.PanelTab, 0, 4);
                settings.PhotoAi = Math.Clamp(s.PhotoAi, 0, MKVision.Desktop.PhotoAi.Models.Length - 1);
                (f.PhotoTexture, f.PhotoGrain) = (Math.Clamp(s.PhotoTexture, 0f, 1f), Math.Clamp(s.PhotoGrain, 0f, 1f));
                (f.PhotoSkin, f.PhotoFullRes, f.PhotoFaceClean) = (s.PhotoSkin, s.PhotoFullRes, s.PhotoFaceClean);
                f.ToneExposure = Math.Clamp(s.ToneExposure, 0f, 1f);
                settings.WebStartup = Math.Clamp(s.WebStartup, 0, 2);
                settings.WebStartupPages = s.WebStartupPages ?? [];
                if (s.WebMobileSites is not null) settings.WebMobileSites = s.WebMobileSites;
                if (s.AdBlockAllowed is not null) settings.AdBlockAllowed = s.AdBlockAllowed;
                settings.AdBlockApplied = s.AdBlockApplied ?? [];
                settings.WebClearOnClose = s.WebClearOnClose;
                settings.WebSmartScreen = s.WebSmartScreen;
                settings.WebSleepMinutes = Math.Clamp(s.WebSleepMinutes, 0, 24 * 60);
                if (s.WebZoom is not null) settings.WebZoom = new(s.WebZoom.Where(z => z.Value >= 0.25 && z.Value <= 5), StringComparer.OrdinalIgnoreCase);
                settings.WebColorScheme = Math.Clamp(s.WebColorScheme, 0, 2);
                settings.WebForceDark = s.WebForceDark;
                settings.LastSaveFolder = s.LastSaveFolder ?? "";
            }
        }
        catch (Exception error)
        {
            Log.Write($"Player: não consegui ler os ajustes guardados ({error.Message}); usando o perfil Pele / Detalhe.");
        }
        return settings;
    }

    public void Save()
    {
        try
        {
            var f = Filter;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Stored
            {
                Detail = f.Detail, DetailRadius = f.DetailRadius, Texture = f.Texture, TextureRadius = f.TextureRadius,
                Threshold = f.Threshold, Deband = f.Deband, Strength = f.Strength, Power = f.Power, Smooth = Smooth,
                RtxVideo = f.RtxVideo, Brightness = f.Brightness, Contrast = f.Contrast, Saturation = f.Saturation, Vibrance = f.Vibrance, Clarity = f.Clarity,
                Volume = Volume, Muted = Muted, ListMode = ListMode, Performance = Performance, Ensemble = Ensemble, Crisp = Crisp, TrainedModel = TrainedModel,
                WebUrl = WebUrl, WebAuto = WebAuto, WebTabs = WebTabs, WebTab = WebTab, WebBookmarksBar = WebBookmarksBar, PanelTab = PanelTab, PhotoAi = PhotoAi, PhotoTexture = f.PhotoTexture, PhotoGrain = f.PhotoGrain, PhotoSkin = f.PhotoSkin, PhotoFullRes = f.PhotoFullRes, PhotoFaceClean = f.PhotoFaceClean, ToneExposure = f.ToneExposure,
                WebStartup = WebStartup, WebStartupPages = WebStartupPages, WebMobileSites = WebMobileSites,
                AdBlockAllowed = AdBlockAllowed, AdBlockApplied = AdBlockApplied, WebClearOnClose = WebClearOnClose, WebSmartScreen = WebSmartScreen,
                WebSleepMinutes = WebSleepMinutes, WebZoom = WebZoom, WebColorScheme = WebColorScheme, WebForceDark = WebForceDark,
                LastSaveFolder = LastSaveFolder
            }));
        }
        catch (Exception error)
        {
            Log.Write($"Player: não consegui guardar os ajustes ({error.Message}).");
        }
    }

    public void Apply(PlayerProfile profile)
    {
        var f = Filter;
        (f.Detail, f.DetailRadius, f.Texture, f.TextureRadius, f.Threshold, f.Deband) =
            (profile.Detail, profile.DetailRadius, profile.Texture, profile.TextureRadius, profile.Threshold, profile.Deband);
    }

    // Perfil cujos valores batem com os controles; null = personalizado.
    public PlayerProfile? CurrentProfile
    {
        get
        {
            var f = Filter;
            static bool Near(float a, float b) => Math.Abs(a - b) < 1e-3;
            return PlayerProfile.All.FirstOrDefault(p => Near(p.Detail, f.Detail) && Near(p.DetailRadius, f.DetailRadius) &&
                Near(p.Texture, f.Texture) && Near(p.TextureRadius, f.TextureRadius) && Near(p.Threshold, f.Threshold) && Near(p.Deband, f.Deband));
        }
    }

    public string ProfileName => CurrentProfile?.Name ?? "Personalizado";

    // Próximo perfil da lista (tecla P).
    public PlayerProfile NextProfile()
    {
        int index = Array.IndexOf(PlayerProfile.All, CurrentProfile);
        var next = PlayerProfile.All[(index + 1) % PlayerProfile.All.Length];
        Apply(next);
        return next;
    }
}
