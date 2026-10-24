using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static MKVision.Desktop.PlayerNative;

namespace MKVision.Desktop;

// Player do MK Vision: abre um arquivo de vídeo no próprio app (sem navegador e sem captura de tela) e mostra
// com a limpeza, a nitidez e a IA de ampliação do app, mais a lupa. Os quadros são os do arquivo, no tamanho
// original e com o tempo exato. Só a imagem.
//
// Câmera lenta suave (abaixo de 1x, tecla S): a IA de quadros (a mesma do modo Edge) analisa os pares que já
// estão na fila, à frente do que está na tela — sem o atraso do modo Edge e com os tempos exatos do arquivo —
// e a tela desenha o quadro intermediário do momento certo.
//
//   Espaço  play/pause      ←/→  5 s (Shift: 1 quadro)      ↑/↓  velocidade 0.1x–2x      Backspace  1x
//   Roda do mouse  zoom no ponto      arrastar  mover      Z / botão do meio  sem zoom
//   F / Enter / duplo clique  tela cheia      Esc  sai da tela cheia      L  repetir      1…5  potência
//   Ctrl+O  abrir (ou arraste um vídeo para a janela)      Ctrl+Home  início      S  câmera lenta suave
//   Home / End  espelha a imagem na horizontal / vertical
//   Mouse: clique no vídeo = play/pause; barra de baixo e menu do topo aparecem com o mouse em cima (PlayerControls)
//   A / Tab  painel de ajustes      P  perfil de nitidez      +/−  força      B  comparar sem processamento
//   Page Up / Page Down  vídeo anterior / próximo da pasta      Insert / Delete  gira 45° (esquerda / direita)
//   Ctrl+Shift+1…4  tamanho dos ícones da lista da pasta (borda direita)
//   I  melhora a imagem da tela com a IA de fotos      U  liga/desliga essa melhoria      T  original ↔ ComfyUI
//   Y  luz e cor (exposição e cor da luz) na imagem da tela; de novo: liga/desliga
//   Ctrl+S  salva a imagem da tela (o quadro, ou a versão da IA/ComfyUI)      Shift+S  salva o vídeo inteiro
//   G  envia o quadro (ou a foto) ao ComfyUI com um prompt (PlayerComfy)      Shift+G  cancela
//
// Os ajustes (perfil, nitidez, força, potência, câmera lenta) são só do player — a extensão não mexe neles — e
// ficam guardados para a próxima vez (PlayerSettings).
//
// Threads: a janela (mensagens) é da thread principal do app; uma thread decodifica à frente (fila de
// quadros na GPU) e outra desenha na taxa da tela, pelo relógio de reprodução.
internal sealed partial class Player : IDisposable
{
    private sealed class Frame
    {
        public required ID3D11Texture2D Texture;
        public required ID3D11ShaderResourceView View;
        public int Width, Height;
        public bool Image;      // foto (textura própria, imutável: não volta para o reaproveitamento)
        public double Time;     // relógio de reprodução (segundos; continua crescendo quando o vídeo repete)
        public double FileTime; // tempo no arquivo
        public long Serial;     // muda quando a textura é reaproveitada para outro quadro
        // Câmera lenta: entrada da IA (RGB planar) e o movimento analisado até o quadro seguinte da fila.
        public float[]? Rgb;
        public int RgbW, RgbH;
        public long RgbSerial = -1;
        public Motion? Motion;
        public bool Analyzed;   // já analisado (ou não dá para analisar: emenda da repetição, lacuna)
        // O quadro já processado (giro, limpeza, IA de ampliação, nitidez) no tamanho e recorte da tela.
        public RenderTexture? Processed;
        public string ProcessedKey = "";
        public long ProcessedSerial = -1;
        // Versão melhorada pela IA de fotos (PlayerPhotoAi): a saída pura dela (o realce de textura é refeito em cima).
        public (byte[] pixels, int w, int h)? Raw;
        // Já tem o giro, o espelho e os efeitos da tela (a do ComfyUI, que recebeu a imagem como a tela mostrava, e as
        // feitas em cima dela): a tela a mostra como está, sem girar nem processar de novo.
        public bool Baked;
    }

    private const string ClassName = "MKVisionPlayer";
    private const int QueueSize = 10; // quadros decodificados à frente
    private const double MinSpeed = 0.1, MaxSpeed = 2.0, MaxZoom = 20;
    private const int MaxAnalysisW = 1920, MaxAnalysisH = 1088; // limite do motor otimizado do TensorRT (Rife.cs)

    // Uma classe de janela para todos os players; as mensagens vão para o player dono da janela.
    private static readonly Native.WndProc StaticProc = Dispatch;
    private static readonly Dictionary<IntPtr, Player> Windows = [];
    private static Player? _creating;
    private static bool _registered;

    public event Action? Closed;
    public IntPtr Hwnd { get; }

    private readonly Gpu _gpu;
    private readonly object _gpuLock;
    private readonly PlayerSettings _prefs;
    private readonly Settings _settings; // = _prefs.Filter (o que a limpeza, a IA e a nitidez leem)
    private PlayerPanel? _panel;
    private readonly Pipeline _pipeline;
    private readonly RenderTexture _output, _interp, _rotated;
    private readonly ID3D11PixelShader _rotateShader;

    [StructLayout(LayoutKind.Sequential)]
    private struct RotateParams { public float SrcW, SrcH, BoxW, BoxH, Cos, Sin, FlipX, FlipY; }

    // Giro (Insert/Delete, passos de 45°; continua ao trocar de vídeo na pasta).
    private int _rotation;
    private bool _flipH, _flipV; // espelho na tela (Home / End), junto com o giro
    private int _settingsVersion; // muda a cada ajuste: os quadros processados com os ajustes antigos são refeitos
    // Vídeos da pasta do vídeo aberto (ordem do Explorer) e qual é o atual: lista da borda direita e Page Up/Down.
    private string[] _folderFiles = [];
    private int _folderIndex = -1;
    private readonly FrameInterpolator _fi;
    private IDXGISwapChain1? _swapChain;
    // Espera da tela (cadeia "com espera"): a thread da tela aguarda o momento de desenhar aqui, fora do Direct3D.
    // Antes, o Present esperava o monitor (~9 ms por quadro) segurando o Direct3D, e a decodificação ficava
    // parada nesse tempo — vídeo de 60 quadros/s andava a ~65% da velocidade.
    private IntPtr _frameWait;

    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObjectEx(IntPtr handle, uint milliseconds, bool alertable);
    private ID3D11Texture2D? _backBuffer;
    private ID3D11RenderTargetView? _backTarget;
    private int _bufferW, _bufferH;

    private readonly object _lock = new();
    // Arquivo e fila de quadros.
    private string? _pendingOpen;
    private VideoFile? _file;
    private bool _image;            // uma foto aberta (_file = null; o único quadro da fila é ela)
    private readonly RenderTexture[] _imageSmall; // foto reduzida para a tela (ProcessFrame)
    private string _fileName = "", _error = "";
    private int _videoW, _videoH;
    private double _duration, _frameDuration = 1 / 30.0;
    private readonly List<Frame> _queue = [], _pool = [];
    private double? _seekRequest;
    private int _seekGeneration;
    private double _loopOffset, _lastQueuedTime;
    private bool _ended, _loop = true;
    // Relógio de reprodução.
    private bool _playing = true;
    private double _speed = 1, _anchorMedia, _anchorWall, _shownFileTime;
    // Janela e lupa.
    private int _clientW, _clientH;
    private bool _resized = true, _dirty = true;
    private double _zoom = 1, _zoomX, _zoomY; // imagem ampliada: posição em frações do retângulo ajustado à janela
    private bool _dragging, _fullscreen, _disposed;
    private int _dragX, _dragY;
    private Native.RECT _windowedRect;
    private string _lastTitle = "";
    // Câmera lenta.
    private bool _smooth;
    private long _nextSerial;
    private (int w, int h) _analysisSize;
    private string _slowInfo = "";
    private int _statNoMotion, _statSlowFrames;
    private double _lateSince; // desde quando a tela espera a análise do par atual (0 = não espera)
    // Diagnóstico da câmera lenta (no log a cada ~3 s): ritmo da tela e custo da IA.
    private double _diagLastPresent, _diagMaxGap, _diagStart, _diagDrawMs, _diagAnalysisMs, _diagPairMs;
    private int _diagFrames, _diagGaps, _diagHeld, _diagSlow, _diagPairs, _diagQuick;

    private readonly AutoResetEvent _decodeWake = new(false), _renderWake = new(false), _analysisWake = new(false);
    private volatile bool _running = true;
    private readonly Thread _decodeThread, _renderThread, _analysisThread;

    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    // Para o autoteste: foto da tela (o que foi desenhado) e o momento do vídeo que ela mostra.
    private ID3D11Texture2D? _snapshot;
    private volatile bool _snapshotWanted;
    private double _snapshotMedia;
    private readonly ManualResetEventSlim _snapshotReady = new(false);

    // Autoteste: os ajustes de imagem (mudar e avisar com TestSettingsChanged).
    public Settings TestFilter => _prefs.Filter;
    public string TestPipelineInfo => _pipeline.LastInfo;
    public bool TestWindowAlive => Native.IsWindow(Hwnd);
    public void TestPhotoKey() => EnhanceShown();
    public void TestToggleKey() => ToggleAi();
    public (bool h, bool v) TestFlip { get { lock (_lock) return (_flipH, _flipV); } }
    public void TestComfyKey() => ToggleComfy();
    public bool TestBypass => _settings.Bypass;
    public void TestToneKey() => ToggleTone();
    public string TestShownName { get { lock (_lock) return ShownName(); } }
    public void TestComfyResult(byte[] pixels, int w, int h)
    {
        long serial;
        lock (_lock) serial = ScreenFrame()?.Serial ?? -1;
        ShowComfy(pixels, w, h, serial);
    }
    // A imagem que o G mandaria agora (a da tela, com giro, espelho e efeitos).
    public (byte[] pixels, int w, int h)? TestComfyPixels()
    {
        lock (_gpuLock)
        lock (_lock)
        {
            if (ScreenFrame() is not { } frame) return null;
            return ScreenPixels(_still is not null && _stillShowAi && _stillSerial == frame.Serial ? _still : frame, ComfyMaxSide);
        }
    }
    public void TestRotate(int degrees) => Rotate(degrees);
    public int TestPhotoModelNow => _prefs.PhotoAi;
    public void TestPhotoModel(int model) { _prefs.PhotoAi = model; OnSettingsChanged(); }
    public void TestSavePhoto() => SavePhotoAi();
    public void TestSaveVideo() => SaveVideo();
    public bool TestStillShown { get { lock (_lock) return _still is not null && _stillShowAi; } }
    // Tamanho de uma versão guardada (a da tela, ou a melhorada/do ComfyUI escondida pelo U/T); (0, 0) = nenhuma.
    public (int w, int h) TestStillSize { get { lock (_lock) return (_still ?? _aiOriginal ?? _aiComfy ?? _comfyLayer) is { } l ? (l.Width, l.Height) : (0, 0); } }
    public bool TestPlaying { get { lock (_lock) return _playing; } }
    public void TestPause() { lock (_lock) { if (_playing) { Reanchor(); _playing = false; } _dirty = true; } _renderWake.Set(); }
    public (int w, int h) TestPhotoSize { get { lock (_lock) return (_videoW, _videoH); } }
    public void TestSettingsChanged() => OnSettingsChanged();

    public (byte[] pixels, int width, int height, double media)? Snapshot()
    {
        _snapshotReady.Reset();
        _snapshotWanted = true;
        lock (_lock) _dirty = true; // parado ou sem vídeo a tela não redesenha sozinha
        _renderWake.Set();
        if (!_snapshotReady.Wait(2000)) return null;
        lock (_gpuLock)
        {
            var d = _snapshot!.Description;
            var pixels = new byte[d.Width * d.Height * 4];
            var map = _gpu.Context.Map(_snapshot, 0, MapMode.Read);
            for (int y = 0; y < d.Height; y++)
                Marshal.Copy(map.DataPointer + (nint)(y * map.RowPitch), pixels, (int)(y * d.Width * 4), (int)d.Width * 4);
            _gpu.Context.Unmap(_snapshot, 0);
            return (pixels, (int)d.Width, (int)d.Height, _snapshotMedia);
        }
    }

    public (int slowFrames, int noMotion, string info) TakeSlowStats()
    {
        lock (_lock)
        {
            var result = (_statSlowFrames, _statNoMotion, _slowInfo);
            _statSlowFrames = _statNoMotion = 0;
            return result;
        }
    }

    // Para o autoteste: quadros apresentados e o que está na tela.
    private long _framesDrawn;
    public long FramesDrawn => Interlocked.Read(ref _framesDrawn);
    public string ProfileName => _prefs.ProfileName;
    public bool PanelVisible => _panel?.Visible == true;
    public IntPtr PanelHwnd => _panel?.Hwnd ?? IntPtr.Zero;
    public PlayerPanel? Panel => _panel;
    public int PanelTabForTest => _prefs.PanelTab;
    public (double fileTime, double speed, double zoom, bool playing, bool fullscreen) State
    {
        get { lock (_lock) return (_shownFileTime, _speed, _zoom, _playing, _fullscreen); }
    }

    // Thread principal do app (a dona das janelas).
    public Player(Gpu gpu, object gpuLock)
    {
        _gpu = gpu;
        _gpuLock = gpuLock;
        _prefs = PlayerSettings.Load();
        _settings = _prefs.Filter;
        _pipeline = new Pipeline(gpu);
        _output = new RenderTexture(gpu.Device, Format.B8G8R8A8_UNorm);
        _interp = new RenderTexture(gpu.Device, Format.R16G16B16A16_Float);
        _rotated = new RenderTexture(gpu.Device, Format.R16G16B16A16_Float);
        _rotateShader = gpu.CreatePixelShader("Rotate.hlsl", "PSRotate");
        _imageSmall = [new RenderTexture(gpu.Device, Format.R16G16B16A16_Float), new RenderTexture(gpu.Device, Format.R16G16B16A16_Float)];
        _fi = new FrameInterpolator(gpu, gpuLock);
        BuildTree();
        // A IA de câmera lenta (TensorRT, pesada para a placa) só carrega quando um vídeo abre: o app começa no
        // navegador e, carregando junto, ela disputava a placa com as páginas.
        if (RifeHost.Started) RifeHost.Use(_prefs.TrainedModel); // o modo Edge pode ter começado antes

        var instance = Native.GetModuleHandle(null);
        if (!_registered)
        {
            var windowClass = new Native.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
                style = CS_DBLCLKS,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(StaticProc),
                hInstance = instance,
                hCursor = LoadCursor(IntPtr.Zero, IDC_ARROW),
                hbrBackground = GetStockObject(BLACK_BRUSH),
                lpszClassName = ClassName,
                // ícone do próprio .exe, no tamanho de cada lugar (barra de tarefas/Alt+Tab e título)
                hIcon = LoadImage(instance, (IntPtr)IDI_APP, IMAGE_ICON, GetSystemMetrics(SM_CXICON), GetSystemMetrics(SM_CXICON), 0),
                hIconSm = LoadImage(instance, (IntPtr)IDI_APP, IMAGE_ICON, GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CXSMICON), 0)
            };
            Native.RegisterClassEx(ref windowClass);
            _registered = true;
        }
        _creating = this;
        Hwnd = Native.CreateWindowEx(WS_EX_ACCEPTFILES, ClassName, "MK Vision", WS_OVERLAPPEDWINDOW | WS_VISIBLE | WS_CLIPCHILDREN,
            CW_USEDEFAULT, CW_USEDEFAULT, 1280, 800, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        _creating = null;
        if (Hwnd == IntPtr.Zero) throw new InvalidOperationException($"não consegui criar a janela do player ({Marshal.GetLastWin32Error()})");
        lock (Windows) Windows[Hwnd] = this;
        _ui = new UiContext(this);
        SynchronizationContext.SetSynchronizationContext(_ui); // o navegador continua nesta thread
        DarkTitleBar(Hwnd);
        _smooth = _prefs.Smooth;
        _photoAiModel = _prefs.PhotoAi;
        _panel = new PlayerPanel(Hwnd, _prefs, OnSettingsChanged, PanelStatus, ZoomStatus, PhotoStatus);
        Native.GetClientRect(Hwnd, out var client);
        (_clientW, _clientH) = (client.Width, client.Height);
        SetTimer(Hwnd, 1, 100, IntPtr.Zero); // título e som (~10x por segundo)
        _audio = new PlayerAudio();
        UpdateTitle();

        _decodeThread = new Thread(DecodeLoop) { IsBackground = true, Name = "Player: decodificação" };
        _renderThread = new Thread(RenderLoop) { IsBackground = true, Name = "Player: tela", Priority = ThreadPriority.AboveNormal };
        _analysisThread = new Thread(AnalysisLoop) { IsBackground = true, Name = "Player: IA de quadros" };
        _decodeThread.Start();
        _renderThread.Start();
        _analysisThread.Start();
        Log.Write($"Player aberto (versão {Updater.Current}): arraste um vídeo para a janela ou use Ctrl+O.");
        ScheduleUpdateCheck(); // versão nova no GitHub? (PlayerUpdate)
    }

    // ---------- Abrir ----------

    // Qualquer thread: troca o vídeo (a decodificação abre o arquivo).
    public void Open(string path)
    {
        (string[] files, int index) = ([], -1);
        if (IsWeb(path) || IsWebKey(path))
        {
            // Imagem da galeria ou item do post do navegador (Page Up/Down): a lista continua a mesma.
            lock (_lock) (files, index) = (_folderFiles, Array.IndexOf(_folderFiles, path));
            if (index < 0) (files, index) = ([path], 0);
        }
        else (files, index) = FolderOf(path);
        lock (_lock)
        {
            if (!IsWeb(path) && !IsWebKey(path)) _fromWeb = false;
            _pendingOpen = path;
            (_folderFiles, _folderIndex) = (files, index);
            _listScroll = -1; // a lista se posiciona no vídeo atual na próxima vez que abrir
        }
        _decodeWake.Set();
    }

    private static readonly string[] VideoExtensions = [".mp4", ".m4v", ".mkv", ".webm", ".mov", ".ts", ".avi", ".wmv"];

    // Vídeo ou foto (a lista da pasta e Page Up/Down passam pelos dois).
    private static bool IsMedia(string path) => VideoExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()) || ImageFile.IsImage(path);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);

    // Vídeos da pasta, na ordem do Explorer ("2" antes de "10"), e a posição deste.
    private static (string[] files, int index) FolderOf(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            var files = Directory.GetFiles(Path.GetDirectoryName(full)!)
                .Where(IsMedia)
                .ToArray();
            Array.Sort(files, (a, b) => StrCmpLogicalW(Path.GetFileName(a), Path.GetFileName(b)));
            return (files, Array.FindIndex(files, f => string.Equals(f, full, StringComparison.OrdinalIgnoreCase)));
        }
        catch
        {
            return ([], -1);
        }
    }

    // Vídeo anterior/próximo da pasta (dá a volta no fim).
    private void OpenSibling(int delta)
    {
        string? next;
        lock (_lock)
        {
            if (_folderFiles.Length == 0) return;
            int index = _folderIndex < 0 ? 0 : ((_folderIndex + delta) % _folderFiles.Length + _folderFiles.Length) % _folderFiles.Length;
            next = _folderFiles[index];
        }
        Open(next);
    }

    // Diálogo "Abrir" numa thread própria (STA, como o Windows pede), sem travar a janela.
    public void ShowOpenDialog()
    {
        var thread = new Thread(() =>
        {
            string? path = AskForFile();
            if (path is not null) Open(path);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static string? AskForFile()
    {
        const int max = 2048;
        var buffer = Marshal.AllocHGlobal(max * 2);
        try
        {
            Marshal.WriteInt16(buffer, 0);
            string folder = Path.Combine(VideoDownload.DownloadsFolder(), "MK Vision");
            var dialog = new OPENFILENAME
            {
                lStructSize = Marshal.SizeOf<OPENFILENAME>(),
                lpstrFilter = "Vídeos e fotos\0*.mp4;*.m4v;*.mkv;*.webm;*.mov;*.ts;*.avi;*.wmv;*.jpg;*.jpeg;*.jfif;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.heic;*.heif;*.avif;*.jxr\0" +
                    "Vídeos\0*.mp4;*.m4v;*.mkv;*.webm;*.mov;*.ts;*.avi;*.wmv\0Fotos\0*.jpg;*.jpeg;*.jfif;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.heic;*.heif;*.avif;*.jxr\0Todos os arquivos\0*.*\0\0",
                lpstrFile = buffer,
                nMaxFile = max,
                lpstrInitialDir = Directory.Exists(folder) ? folder : null,
                lpstrTitle = "MK Vision — abrir vídeo",
                Flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR
            };
            return GetOpenFileName(ref dialog) ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // Traz a janela para a frente mesmo com outro programa em foco (ex.: o Edge, quando a extensão pede para abrir
    // um vídeo): o Windows só deixa quem tem o foco mudá-lo, então a thread se "anexa" à da janela da frente.
    public void BringToFront()
    {
        if (Native.IsIconic(Hwnd)) Native.ShowWindow(Hwnd, 9 /* SW_RESTORE */);
        var foreground = Native.GetForegroundWindow();
        uint foregroundThread = Native.GetWindowThreadProcessId(foreground, out _), thread = GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != thread && AttachThreadInput(thread, foregroundThread, true);
        SetForegroundWindow(Hwnd);
        BringWindowToTop(Hwnd);
        if (attached) AttachThreadInput(thread, foregroundThread, false);
    }

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint thread, uint to, bool attach);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);

    // ---------- Decodificação (thread própria) ----------

    // Quadros repetidos: muitos vídeos guardam cópias da mesma imagem (ex.: 15 imagens por segundo num arquivo
    // de 30, às vezes em ritmo irregular, 1 quadro e depois 3). Em câmera lenta cada cópia vira a imagem
    // parada por um bom tempo — "roda e para". As cópias são descartadas e cada imagem real fica no meio do
    // tempo em que aparece no arquivo: o ritmo volta a ser regular e a IA cria o movimento entre imagens reais.
    // Uma imagem fica retida até chegar a seguinte (para saber quanto ela dura).
    internal static List<(double time, bool repeated)>? DedupeTrace; // autoteste: cada quadro lido e a decisão
    private const int MaxRepeats = 3;              // no máximo 3 cópias seguidas (cena parada de verdade continua andando)
    private const int SignatureW = 160, SignatureH = 90;
    private RenderTexture? _signatureTexture;
    private readonly ID3D11Texture2D?[] _signatureStaging = new ID3D11Texture2D?[SignatureSlots];
    private int _signatureNext;
    private const int SignatureSlots = 4; // miniaturas a caminho (a leitura acontece 2 quadros depois)

    // Miniatura do quadro (média de área), para comparar com o anterior. Thread da decodificação. Em duas partes:
    // pede à placa (não espera) e, dois quadros depois, lê — esperar a placa a cada quadro levava ~30 ms, mais
    // que o tempo de um quadro de vídeo a 60 quadros/s.
    private int RequestSignature(Frame frame)
    {
        int slot = _signatureNext++ % SignatureSlots;
        lock (_gpuLock)
        {
            _signatureTexture ??= new RenderTexture(_gpu.Device, Format.B8G8R8A8_UNorm);
            _signatureTexture.Ensure(SignatureW, SignatureH);
            _signatureStaging[slot] ??= _gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, SignatureW, SignatureH, 1, 1,
                BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
            _fi.Resample(frame.View, _signatureTexture);
            _gpu.Context.CopyResource(_signatureStaging[slot], _signatureTexture.Texture!);
            _gpu.Context.Flush();
        }
        return slot;
    }

    private byte[] ReadSignature(int slot)
    {
        var signature = new byte[SignatureW * SignatureH * 4];
        _fi.ReadStaging(_signatureStaging[slot]!, SignatureW, SignatureH, signature);
        return signature;
    }

    // Mesma imagem: nenhum ponto da miniatura mudou mais que 3 níveis e a média quase zero (a cópia costuma
    // sair idêntica do decodificador; movimento real muda bem mais que isso em algum lugar).
    private static bool SameImage(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        long sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if ((i & 3) == 3) continue; // alfa
            int d = Math.Abs(a[i] - b[i]);
            if (d > 3) return false;
            sum += d;
        }
        return sum < a.Length * 3 / 4 * 0.3;
    }

    private void DecodeLoop()
    {
        double skipUntil = 0;
        // Imagem retida (ver MaxRepeats).
        Frame? held = null;
        byte[] heldSignature = [];
        double heldTime = 0;
        int heldRepeats = 0, heldGeneration = 0, repeats = 0, seen = 0;
        VideoFile? heldFile = null;
        // Quadros esperando a miniatura (lida dois quadros depois).
        var waiting = new Queue<(Frame frame, double time, int slot, int generation, VideoFile file)>();

        void Drop()
        {
            if (held is not null) lock (_lock) _pool.Add(held);
            held = null;
            while (waiting.TryDequeue(out var w)) lock (_lock) _pool.Add(w.frame);
        }

        // Cópia da imagem retida? Descarta; imagem nova: entrega a retida e passa a reter esta.
        void Decide((Frame frame, double time, int slot, int generation, VideoFile file) w)
        {
            lock (_lock)
            {
                if (_seekGeneration != w.generation || _file != w.file)
                {
                    _pool.Add(w.frame);
                    return;
                }
            }
            var signature = ReadSignature(w.slot);
            bool repeated = held is not null && heldGeneration == w.generation && heldFile == w.file &&
                            heldRepeats < MaxRepeats && SameImage(signature, heldSignature);
            DedupeTrace?.Add((w.time, repeated));
            if (++seen <= 300)
            {
                if (repeated) repeats++;
                if (seen == 300 && repeats > 30)
                    Log.Write($"Player: o vídeo tem quadros repetidos ({repeats} de {seen}); a câmera lenta usa só as imagens novas.");
            }
            if (repeated)
            {
                heldRepeats++;
                lock (_lock) _pool.Add(w.frame); // cópia: a imagem retida continua valendo
                return;
            }
            if (held is not null) Release(w.time);
            (held, heldSignature, heldTime, heldRepeats, heldGeneration, heldFile) = (w.frame, signature, w.time, 0, w.generation, w.file);
        }

        // Entrega a imagem retida à fila; "end" = tempo do arquivo em que ela deixa de aparecer.
        void Release(double end)
        {
            var frame = held!;
            held = null;
            lock (_lock)
            {
                if (_seekGeneration != heldGeneration || _file != heldFile)
                {
                    _pool.Add(frame);
                    return;
                }
                double time = heldTime + Math.Max(0, end - heldTime - _frameDuration) / 2;
                frame.FileTime = time;
                frame.Time = time + _loopOffset;
                _lastQueuedTime = frame.Time;
                _queue.Add(frame);
                _dirty = true;
            }
            _renderWake.Set();
            _analysisWake.Set();
        }

        while (_running)
        {
            try
            {
                string? open;
                double? seek;
                VideoFile? file;
                int generation;
                lock (_lock)
                {
                    open = _pendingOpen;
                    _pendingOpen = null;
                    file = _file;
                    seek = _seekRequest;
                    _seekRequest = null;
                    generation = _seekGeneration;
                }
                if (open is not null)
                {
                    Drop();
                    OpenNow(open);
                    skipUntil = 0;
                    repeats = seen = 0;
                    continue;
                }
                if (file is null)
                {
                    _decodeWake.WaitOne(200);
                    continue;
                }
                if (seek is { } target)
                {
                    Drop();
                    try { file.Seek(target); }
                    catch when (_webReopen is not null)
                    {
                        // Vídeo baixando: o ponto ainda não estava no arquivo aberto. Espera chegar e reabre.
                        if (ReopenFor(target, file, generation) is not { } fresh) continue;
                        file = fresh;
                        file.Seek(target);
                    }
                    lock (_lock)
                    {
                        _pool.AddRange(_queue);
                        _queue.Clear();
                    }
                    skipUntil = target - _frameDuration * 0.5; // o Windows volta ao quadro-chave: descarta até o alvo
                }
                bool wait;
                lock (_lock) wait = _ended || _queue.Count >= QueueSize;
                if (wait)
                {
                    _decodeWake.WaitOne(50);
                    continue;
                }

                Frame? frame = null;
                if (!file.ReadFrame((source, index) => frame = CopyFrame(source, index, file), out double time))
                {
                    // Vídeo baixando: é só o fim do que tinha baixado ao abrir. Reabre (maior) e segue do mesmo ponto.
                    if (_webReopen is not null && (_webGrowTask?.IsCompleted != true || (File.Exists(_webGrowPath) && new FileInfo(_webGrowPath).Length > _webOpenedLength)))
                    {
                        if (ReopenGrowing(file) is { } fresh)
                        {
                            fresh.Seek(_lastDecodedTime);
                            skipUntil = _lastDecodedTime + _frameDuration * 0.5;
                        }
                        else Thread.Sleep(150); // ainda não chegou mais nada
                        continue;
                    }
                    // Fim do arquivo: repete emendando (o relógio continua), ou para no último quadro.
                    while (waiting.TryDequeue(out var w)) Decide(w);
                    if (held is not null) Release(heldTime + _frameDuration * (1 + heldRepeats));
                    bool loop;
                    lock (_lock)
                    {
                        loop = _loop && _seekGeneration == generation;
                        if (loop) _loopOffset = _lastQueuedTime + _frameDuration;
                        else _ended = true;
                    }
                    if (loop)
                    {
                        file.Seek(0);
                        skipUntil = 0;
                    }
                    continue;
                }
                if (frame is null) continue;
                _lastDecodedTime = time;
                bool keep = time >= skipUntil;
                lock (_lock)
                {
                    if (!keep || _seekGeneration != generation || _file != file)
                    {
                        _pool.Add(frame); // antes do alvo, ou o usuário pulou para outro ponto enquanto lia
                        continue;
                    }
                }
                waiting.Enqueue((frame, time, RequestSignature(frame), generation, file));
                while (waiting.Count > 2) Decide(waiting.Dequeue());
            }
            catch (Exception error)
            {
                Log.Write($"Player: falha na decodificação — {error.Message}");
                lock (_lock)
                {
                    _error = error.Message;
                    _ended = true;
                }
                Thread.Sleep(500);
            }
        }
    }

    private string _photoPath = ""; // foto aberta (para a IA de fotos refazer com outro modelo)

    private void OpenNow(string path)
    {
        StartSlowAi();
        _photoJob?.Cancel(); // outra coisa abriu: a IA de fotos da anterior para
        if (IsWeb(path))
        {
            // Imagem do navegador: baixa e abre como foto.
            try { OpenImage(DownloadImage(path), WebName(path)); }
            catch (Exception e)
            {
                Log.Write($"Player: não consegui baixar a imagem — {e.Message}");
                Notify($"Não consegui abrir a imagem: {e.Message}");
            }
            return;
        }
        if (ImageFile.IsImage(path))
        {
            OpenImage(path);
            return;
        }
        VideoFile? file = null;
        string error = "", name = Path.GetFileName(path);
        string? webAudio = null;
        WebMedia? media;
        lock (_lock) _webMedia.TryGetValue(path, out media); // fica: Page Up/Down pode voltar a ele (post com vários)
        if (media is not null)
        {
            name = media.Title;
            (file, error, webAudio) = OpenWebVideo(media);
            if (file is null) Notify($"Não consegui tocar: {error}");
        }
        else
        {
            try
            {
                file = new VideoFile(path, _gpu.Device);
            }
            catch (Exception e)
            {
                error = e.Message;
                Log.Write($"Player: não consegui abrir o arquivo — {e.Message}");
            }
        }
        VideoFile? old;
        lock (_gpuLock)
        lock (_lock)
        {
            old = _file;
            _file = file;
            _image = false;
            RecycleQueue();
            _error = error;
            _fileName = name;
            if (file is not null)
            {
                _audioOpen = media is null ? path : webAudio; // o som abre na thread principal
                (_videoW, _videoH) = (file.Width, file.Height);
                _duration = media is { Duration: > 0 } ? media.Duration : file.Duration; // site: a duração que o yt-dlp informa (o arquivo ainda está baixando)
                _frameDuration = file.FrameDuration;
            }
            _anchorMedia = 0;
            _anchorWall = Now;
            _loopOffset = _lastQueuedTime = _shownFileTime = 0;
            _ended = false;
            _playing = true;
            _zoom = 1;
            _zoomX = _zoomY = 0;
            (_crop, _cropBaked, _cropping) = (FullCrop, FullCrop, false);
            _seekRequest = null;
            _seekGeneration++;
            _dirty = true;
        }
        old?.Dispose();
        _renderWake.Set();
        if (file is not null)
        {
            int index, count;
            lock (_lock) (index, count) = (_folderIndex, _folderFiles.Length);
            string shown = media is not null ? name : Path.GetFileNameWithoutExtension(path);
            Notify(count > 1 && index >= 0 ? $"{index + 1}/{count}  ·  {shown}" : shown);
        }
        if (file is not null)
            Log.Write($"Player: vídeo aberto — {file.Width}x{file.Height}, {1 / file.FrameDuration:0.##} quadros/s, {TimeText(file.Duration)}" +
                      (media is not null ? (media.Hls ? " (internet, HLS)" : " (internet)") : ""));
    }

    // Com o lock da GPU e _lock: a fila volta para o reaproveitamento; a foto (textura imutável) é descartada.
    private void RecycleQueue()
    {
        foreach (var frame in _queue)
        {
            if (!frame.Image)
            {
                _pool.Add(frame);
                continue;
            }
            frame.Processed?.Dispose();
            frame.View.Dispose();
            frame.Texture.Dispose();
        }
        _queue.Clear();
    }

    // Foto: um quadro só, parado. A limpeza, a IA de ampliação e a nitidez rodam como no vídeo — e de novo a
    // cada zoom, na parte que aparece na tela (zoom sem perder qualidade).
    private void OpenImage(string path, string? displayName = null)
    {
        Frame? frame = null;
        string error = "";
        var clock = Stopwatch.StartNew();
        try
        {
            var (texture, w, h) = ImageFile.Load(path, _gpu.Device);
            frame = new Frame { Texture = texture, View = _gpu.Device.CreateShaderResourceView(texture), Width = w, Height = h, Image = true, Analyzed = true };
        }
        catch (Exception e)
        {
            error = e.Message;
            Log.Write($"Player: não consegui abrir o arquivo — {e.Message}");
        }
        VideoFile? old;
        lock (_gpuLock)
        lock (_lock)
        {
            old = _file;
            _file = null;
            _image = frame is not null;
            RecycleQueue();
            _error = error;
            _fileName = displayName ?? Path.GetFileName(path);
            _photoPath = path;
            if (frame is not null)
            {
                frame.Serial = ++_nextSerial;
                _queue.Add(frame);
                (_videoW, _videoH) = (frame.Width, frame.Height);
            }
            _duration = 0;
            _anchorMedia = 0;
            _anchorWall = Now;
            _loopOffset = _lastQueuedTime = _shownFileTime = 0;
            _ended = false;
            _playing = false;
            _zoom = 1;
            _zoomX = _zoomY = 0;
            (_crop, _cropBaked, _cropping) = (FullCrop, FullCrop, false);
            _seekRequest = null;
            _seekGeneration++;
            _dirty = true;
        }
        old?.Dispose();
        _renderWake.Set();
        if (frame is null) return;
        int index, count;
        lock (_lock) (index, count) = (_folderIndex, _folderFiles.Length);
        string shown = Path.GetFileNameWithoutExtension(displayName ?? path);
        Notify(count > 1 && index >= 0 ? $"{index + 1}/{count}  ·  {shown}" : shown);
        Log.Write($"Player: foto aberta — {frame.Width}x{frame.Height} ({clock.ElapsedMilliseconds} ms)");
        _photoJob?.Cancel(); // a IA de fotos só roda com o I (a da foto anterior para aqui)
        lock (_lock) _photoStatus = "";
    }

    // Copia o quadro decodificado para uma textura nossa (o decodificador reaproveita as dele).
    private Frame CopyFrame(ID3D11Texture2D source, uint index, VideoFile file)
    {
        var description = source.Description;
        int w = Math.Min(file.Width, (int)description.Width), h = Math.Min(file.Height, (int)description.Height);
        lock (_gpuLock)
        {
            Frame? frame;
            lock (_lock)
            {
                frame = _pool.Count > 0 ? _pool[^1] : null;
                if (frame is not null) _pool.RemoveAt(_pool.Count - 1);
                (_videoW, _videoH) = (w, h);
            }
            if (frame is not null && (frame.Width != w || frame.Height != h || frame.Texture.Description.Format != description.Format))
            {
                frame.View.Dispose();
                frame.Texture.Dispose();
                frame = null;
            }
            if (frame is null)
            {
                var texture = _gpu.Device.CreateTexture2D(new Texture2DDescription(description.Format, (uint)w, (uint)h, 1, 1, BindFlags.ShaderResource));
                frame = new Frame { Texture = texture, View = _gpu.Device.CreateShaderResourceView(texture), Width = w, Height = h };
            }
            _gpu.Context.CopySubresourceRegion(frame.Texture, 0, 0, 0, 0, source, index, new Box(0, 0, 0, w, h, 1));
            lock (_lock)
            {
                frame.Serial = ++_nextSerial;
                frame.Motion?.Dispose(); // com o lock da GPU: a tela não está usando
                frame.Motion = null;
                frame.Analyzed = false;
            }
            return frame;
        }
    }

    // ---------- Tela (thread própria, na taxa do monitor) ----------

    private void RenderLoop()
    {
        while (_running)
        {
            bool active, dirty;
            lock (_lock)
            {
                active = (_playing && _file is not null) || OsdActive();
                dirty = _dirty;
                _dirty = false;
            }
            if (!active && !dirty)
            {
                _renderWake.WaitOne(100); // pausado: só redesenha quando algo muda
                continue;
            }
            if (Native.IsIconic(Hwnd))
            {
                Thread.Sleep(50);
                continue;
            }
            try
            {
                if (_frameWait != IntPtr.Zero) WaitForSingleObjectEx(_frameWait, 100, true);
                if (!Draw()) _renderWake.WaitOne(20);
            }
            catch (Exception error)
            {
                Log.Write($"Player: falha ao desenhar — {error.Message}");
                Thread.Sleep(500);
            }
        }
    }

    private bool Draw()
    {
        double drawStart = Now;
        lock (_gpuLock)
        {
            int cw, ch;
            lock (_lock) (cw, ch) = (_clientW, _clientH);
            if (cw < 16 || ch < 16) return false;
            var target = BackBuffer(cw, ch);
            _gpu.Context.ClearRenderTargetView(target, new Color4(0, 0, 0, 1));

            Frame? frame, next = null;
            bool still = false;
            int rotation;
            (float x, float y) flip;
            lock (_lock) (rotation, flip) = (_rotation, (_flipH ? -1 : 1, _flipV ? -1 : 1));
            Motion? motion = null;
            float s = 0;
            bool slow = false;
            VideoView view = default;
            Native.RECT output = default;
            var crop = FullCrop;
            bool cropping = false;
            bool freed = false;
            lock (_lock)
            {
                double media = MediaNow();
                // Mostra o último quadro cuja hora já chegou; os anteriores voltam para a decodificação.
                while (_queue.Count >= 2 && _queue[1].Time <= media)
                {
                    _pool.Add(_queue[0]);
                    _queue.RemoveAt(0);
                    freed = true;
                }
                frame = _queue.Count > 0 ? _queue[0] : null;
                // Versões da imagem (IA de fotos / ComfyUI, PlayerPhotoAi): a escolhida no lugar do quadro (ou da foto), até
                // dar play, pular ou abrir outra.
                if (HasLayers && (_playing || frame?.Serial != _stillSerial || (_file is null && !_image))) DisposeStill();
                if (frame is not null && _still is not null && _stillShowAi)
                {
                    _shownFileTime = frame.FileTime;
                    frame = _still;
                    still = true;
                }
                cropping = _cropping;
                if (frame is not null && still)
                {
                    if (frame.Baked) (rotation, flip) = (0, (1, 1));
                    crop = cropping ? FullCrop : CropFor(frame); // F4: no modo de corte, a imagem inteira
                    (view, output) = Layout(cw, ch, frame.Width, frame.Height, rotation, crop);
                }
                else if (frame is not null)
                {
                    _shownFileTime = frame.FileTime;
                    if (_ended && _queue.Count == 1 && media > frame.Time + _frameDuration)
                    {
                        _anchorMedia = frame.Time; // fim, sem repetir: o relógio para no último quadro
                        _anchorWall = Now;
                        _playing = false;
                    }
                    crop = cropping ? FullCrop : CropFor(frame);
                    (view, output) = Layout(cw, ch, frame.Width, frame.Height, rotation, crop);
                    // Câmera lenta: entre este quadro e o próximo, no momento exato do relógio.
                    next = _queue.Count > 1 ? _queue[1] : null;
                    slow = SlowActive() && next is not null && next.FileTime > frame.FileTime && next.Width == frame.Width && next.Height == frame.Height;
                    if (slow)
                    {
                        motion = frame.Motion is { } m && m.Key == next ? m : null;
                        // A análise deste par ainda vem (logo depois de abrir ou pular): o relógio espera no quadro
                        // em vez de seguir — senão, quando ela chega, a imagem pula do quadro parado para o meio do
                        // caminho e partes "somem e aparecem" em outro lugar. No máximo 1 s por espera.
                        if (motion is null && !frame.Analyzed)
                        {
                            if (_lateSince == 0) _lateSince = Now;
                            if (Now - _lateSince < 1.0 && media > frame.Time)
                            {
                                _anchorMedia = media = frame.Time;
                                _anchorWall = Now;
                            }
                        }
                        else _lateSince = 0;
                        s = motion is null ? 0 : (float)Math.Clamp((media - frame.Time) / (next!.Time - frame.Time), 0, 1);
                        _statSlowFrames++;
                        if (motion is null) _statNoMotion++; // análise atrasada: mostra o quadro real
                        _diagSlow++;
                        if (motion is null) _diagHeld++;
                    }
                }
                if (frame is not null) NoteCropFrame(frame, rotation, cw, ch); // F4: onde a imagem inteira está
            }
            if (freed) _decodeWake.Set();

            bool web;
            lock (_lock) web = _webMode;
            if (frame is not null && !web && output.Width > 0 && output.Height > 0)
            {
                // O pesado (giro, limpeza, IA de ampliação, nitidez) roda uma vez por quadro real, no tamanho e
                // recorte da tela, e fica guardado no quadro. Cada quadro de tela só copia — ou, na câmera lenta,
                // cria o intermediário entre os dois quadros já processados (leve). Antes, tudo rodava 144x/s.
                string key = $"{output.Width}x{output.Height}|{view.UvX:R},{view.UvY:R},{view.UvWidth:R},{view.UvHeight:R}|{rotation}|{flip}|{_settingsVersion}";
                var settings = still && frame.Baked ? BakedSettings : _settings;
                ProcessFrame(frame, key, view, output, rotation, flip, settings);
                if (slow && motion is not null)
                {
                    ProcessFrame(next!, key, view, output, rotation, flip, settings);
                    _output.Ensure(output.Width, output.Height);
                    var (mapX, mapY, delta) = ViewMapping(view, frame.Width, frame.Height, rotation, flip);
                    FrameInterpolator.CrispLevel = _prefs.Crisp ? 1 : 0;
                    _fi.DrawView(_output, frame.Processed!.View!, next!.Processed!.View!, motion, s, mapX, mapY, delta);
                    _gpu.Context.CopySubresourceRegion(_backBuffer!, 0, (uint)output.Left, (uint)output.Top, 0, _output.Texture!, 0, null);
                }
                else
                {
                    _gpu.Context.CopySubresourceRegion(_backBuffer!, 0, (uint)output.Left, (uint)output.Top, 0, frame.Processed!.Texture!, 0, null);
                }
            }
            if (frame is not null && !web && output.Width > 0 && output.Height > 0)
            {
                string shownKey;
                // A imagem (foto ou quadro) e a versão dela: a resposta do ComfyUI é outra versão (os traços de uma não
                // aparecem na outra).
                lock (_lock) shownKey = (_image ? $"foto|{_fileName}" : $"vídeo|{_fileName}|{_shownFileTime:F3}") +
                                        (still && frame.Baked ? $"#comfy{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(frame)}" : "#");
                DrawAnnotations(target, cw, ch, frame.Width, frame.Height, rotation, flip, crop, shownKey); // F3 (PlayerAnnotate)
                if (cropping) DrawCrop(target, cw, ch); // F4 (PlayerCrop)
            }
            DrawInterface(target, cw, ch); // barra de controles, menu do topo, aviso sem vídeo
            if (_snapshotWanted)
            {
                var d = _backBuffer!.Description;
                if (_snapshot is null || _snapshot.Description.Width != d.Width || _snapshot.Description.Height != d.Height)
                {
                    _snapshot?.Dispose();
                    _snapshot = _gpu.Device.CreateTexture2D(new Texture2DDescription(d.Format, d.Width, d.Height, 1, 1,
                        BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
                }
                _gpu.Context.CopyResource(_snapshot, _backBuffer);
                lock (_lock) _snapshotMedia = MediaNow();
                _snapshotWanted = false;
                _snapshotReady.Set();
            }
        }
        double drawMs = (Now - drawStart) * 1000;
        _swapChain!.Present(1, PresentFlags.None);
        Interlocked.Increment(ref _framesDrawn);
        double now = Now;
        lock (_lock)
        {
            if (_diagLastPresent > 0)
            {
                double gap = (now - _diagLastPresent) * 1000;
                _diagMaxGap = Math.Max(_diagMaxGap, gap);
                if (gap > 40) _diagGaps++;
            }
            _diagLastPresent = now;
            _diagFrames++;
            _diagDrawMs = Math.Max(_diagDrawMs, drawMs);
        }
        return true;
    }

    // Uma versão "pronta" (Frame.Baked) aparece sem os efeitos: só a escala até a tela.
    private static readonly Settings BakedSettings = new() { Bypass = true };

    // Processa o quadro (se ainda não foi com estes ajustes, este recorte e este giro). Com o lock da GPU.
    private void ProcessFrame(Frame frame, string key, VideoView view, Native.RECT output, int rotation, (float x, float y) flip, Settings settings)
    {
        if (frame.Processed is not null && frame.ProcessedKey == key && frame.ProcessedSerial == frame.Serial) return;
        frame.Processed ??= new RenderTexture(_gpu.Device, Format.B8G8R8A8_UNorm);
        ProcessInto(frame, frame.Processed, view, output.Width, output.Height, rotation, flip, settings);
        frame.ProcessedKey = key;
        frame.ProcessedSerial = frame.Serial;
    }

    // O processamento da tela (giro, espelho, limpeza, IA de ampliação, nitidez, Cor e luz) do quadro para "target",
    // no tamanho pedido. Com o lock da GPU.
    private void ProcessInto(Frame frame, RenderTexture target, VideoView view, int outW, int outH, int rotation, (float x, float y) flip, Settings settings)
    {
        target.Ensure(outW, outH);
        var source = frame.View;
        int sw = frame.Width, sh = frame.Height;
        if (rotation != 0 || flip != (1, 1))
        {
            var (bw, bh) = Box(sw, sh, rotation);
            _rotated.Ensure(bw, bh);
            double radians = rotation * Math.PI / 180;
            _gpu.BeginPasses();
            _gpu.SetConstants(new RotateParams { SrcW = sw, SrcH = sh, BoxW = bw, BoxH = bh, Cos = (float)Math.Cos(radians), Sin = (float)Math.Sin(radians), FlipX = flip.x, FlipY = flip.y });
            _gpu.Context.PSSetSampler(0, _gpu.LinearSampler);
            _gpu.Draw(_rotateShader, _rotated.Target!, bw, bh, source);
            (source, sw, sh) = (_rotated.View!, bw, bh);
        }
        if (frame.Image)
        {
            // Foto maior que a tela (ou que a parte dela na tela): reduz antes pela média de área, em passos de até
            // 4x — direto, a redução serrilharia. Com zoom além de 1:1 a foto vai inteira e a IA amplia.
            double scale = outW / (view.UvWidth * sw);
            while (scale < 0.98)
            {
                double step = Math.Max(scale, 0.25);
                var next = source == _imageSmall[0].View ? _imageSmall[1] : _imageSmall[0];
                int dw = Math.Max(1, (int)Math.Round(sw * step)), dh = Math.Max(1, (int)Math.Round(sh * step));
                next.Ensure(dw, dh);
                _fi.Resample(source, next);
                (source, sw, sh) = (next.View!, dw, dh);
                scale /= step;
            }
        }
        _pipeline.Finish(source, sw, sh, view, target.Target!, outW, outH, settings, resized: false, allowTemporal: false);
    }

    // A imagem como a tela mostra (a versão escolhida, com giro, espelho, corte e efeitos), inteira (sem o zoom) e no
    // tamanho real, até "maxSide" de lado. Com _lock e o lock da GPU.
    private (byte[] pixels, int w, int h) ScreenPixels(Frame frame, int maxSide, bool annotations = true)
    {
        int rotation = frame.Baked ? 0 : _rotation;
        (float x, float y) flip = frame.Baked ? (1, 1) : (_flipH ? -1 : 1, _flipV ? -1 : 1);
        var (bw, bh) = Box(frame.Width, frame.Height, rotation);
        var crop = CropFor(frame); // F4: só a parte cortada
        int cropW = CropSize(bw, crop.w), cropH = CropSize(bh, crop.h);
        double scale = Math.Min(1, (double)maxSide / Math.Max(cropW, cropH));
        int w = Math.Max(1, (int)Math.Round(cropW * scale)), h = Math.Max(1, (int)Math.Round(cropH * scale));
        using var target = new RenderTexture(_gpu.Device, Format.B8G8R8A8_UNorm);
        ProcessInto(frame, target, new VideoView(default, bw, bh, (float)crop.x, (float)crop.y, (float)crop.w, (float)crop.h), w, h, rotation, flip,
            frame.Baked ? BakedSettings : _settings);
        var pixels = ReadPixels(target.Texture!, w, h);
        if (annotations) BurnAnnotationsShown(pixels, w, h, frame); // as anotações (F3) também vão: fazem parte da tela
        return (pixels, w, h);
    }

    // Da tela (0–1 no recorte desenhado) para o vídeo (0–1), passando pela lupa e pelo giro; e o inverso para
    // deslocamentos (o movimento da IA está no vídeo, o intermediário é criado na tela).
    private static ((double, double, double) mapX, (double, double, double) mapY, (double, double, double, double) delta)
        ViewMapping(VideoView view, int sw, int sh, int rotation, (float x, float y) flip)
    {
        double r = rotation * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
        // Caixa girada (o que o recorte usa); o espelho inverte o eixo dela, como no Rotate.hlsl.
        double bw = view.NativeWidth * flip.x, bh = view.NativeHeight * flip.y;
        double ux = view.UvX - 0.5, uy = view.UvY - 0.5;
        var mapX = (c * bw * view.UvWidth / sw, s * bh * view.UvHeight / sw, (c * bw * ux + s * bh * uy) / sw + 0.5);
        var mapY = (-s * bw * view.UvWidth / sh, c * bh * view.UvHeight / sh, (-s * bw * ux + c * bh * uy) / sh + 0.5);
        double a = mapX.Item1, b = mapX.Item2, d0 = mapY.Item1, d1 = mapY.Item2, det = a * d1 - b * d0;
        return (mapX, mapY, (d1 / det, -b / det, -d0 / det, a / det));
    }

    private ID3D11RenderTargetView BackBuffer(int width, int height)
    {
        if (_swapChain is null)
        {
            using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
            _swapChain = factory.CreateSwapChainForHwnd(_gpu.Device, Hwnd, new SwapChainDescription1
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = Format.B8G8R8A8_UNorm,
                BufferCount = 2,
                BufferUsage = Usage.RenderTargetOutput,
                SampleDescription = SampleDescription.Default,
                SwapEffect = SwapEffect.FlipDiscard,
                Flags = SwapChainFlags.FrameLatencyWaitableObject,
                AlphaMode = AlphaMode.Ignore,
                Scaling = Scaling.Stretch
            });
            using (var waitable = _swapChain.QueryInterface<IDXGISwapChain2>())
            {
                waitable.MaximumFrameLatency = 1;
                _frameWait = waitable.FrameLatencyWaitableObject;
            }
            factory.MakeWindowAssociation(Hwnd, WindowAssociationFlags.IgnoreAltEnter); // tela cheia é a nossa (F)
            (_bufferW, _bufferH) = (width, height);
        }
        else if (width != _bufferW || height != _bufferH)
        {
            _backTarget?.Dispose();
            _backBuffer?.Dispose();
            _backTarget = null;
            _backBuffer = null;
            _swapChain.ResizeBuffers(2, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, SwapChainFlags.FrameLatencyWaitableObject).CheckError();
            (_bufferW, _bufferH) = (width, height);
        }
        if (_backTarget is null)
        {
            _backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            _backTarget = _gpu.Device.CreateRenderTargetView(_backBuffer);
        }
        return _backTarget;
    }

    // ---------- Relógio ----------

    // Com _lock.
    private double MediaNow() => _playing ? _anchorMedia + (Now - _anchorWall) * _speed : _anchorMedia;

    private void Reanchor()
    {
        _anchorMedia = MediaNow();
        _anchorWall = Now;
    }

    private void TogglePlay()
    {
        bool restart;
        lock (_lock)
        {
            if (_file is null) return;
            Reanchor();
            restart = !_playing && _ended; // terminou sem repetir: play recomeça do início
            if (!restart) _playing = !_playing;
        }
        if (!restart)
        {
            Notify(_playing ? "Tocando" : "Pausado");
            return;
        }
        SeekTo(0);
        lock (_lock) _playing = true;
        Notify("Do início");
    }

    private void SetSpeed(double speed)
    {
        lock (_lock)
        {
            Reanchor();
            _speed = Math.Round(Math.Clamp(speed, MinSpeed, MaxSpeed), 2);
        }
        Notify($"Velocidade {Dot(_speed, "0.0#")}x" + (_speed < 0.999 && _prefs.Smooth ? " · câmera lenta IA" : ""));
    }

    private void SeekTo(double fileTime)
    {
        lock (_lock)
        {
            if (_file is null) return;
            double end = _duration > 0 ? Math.Max(0, _duration - _frameDuration) : double.MaxValue;
            double t = Math.Clamp(fileTime, 0, end);
            _seekRequest = t;
            _seekGeneration++;
            _loopOffset = 0;
            _anchorMedia = t;
            _anchorWall = Now;
            _ended = false;
            _dirty = true;
        }
        _decodeWake.Set();
    }

    // Quadro a quadro (pausa). Para frente: só adianta o relógio até o próximo quadro da fila.
    private void StepFrame(int direction)
    {
        double back;
        lock (_lock)
        {
            if (_file is null) return;
            Reanchor();
            _playing = false;
            if (direction > 0)
            {
                _anchorMedia = _queue.Count > 1 ? _queue[1].Time : _anchorMedia + _frameDuration;
                _dirty = true;
            }
            back = _shownFileTime - _frameDuration;
        }
        Notify(direction > 0 ? "Próximo quadro" : "Quadro anterior");
        if (direction < 0) SeekTo(back);
    }

    // ---------- Lupa ----------

    // Caixa que contém o vídeo girado (90° = lados trocados; 45° = quadrado maior).
    private static (int w, int h) Box(int w, int h, int rotation)
    {
        if (rotation % 180 == 0) return (w, h);
        if (rotation % 90 == 0) return (h, w);
        double r = rotation * Math.PI / 180, c = Math.Abs(Math.Cos(r)), s = Math.Abs(Math.Sin(r));
        return ((int)Math.Ceiling(w * c + h * s), (int)Math.Ceiling(w * s + h * c));
    }

    // Encaixe do vídeo (girado) na janela atual. Com _lock.
    private (double x, double y, double w, double h) FitDisplay()
    {
        bool baked = _still is { Baked: true } && _stillShowAi;
        var (bw, bh) = Box(_videoW, _videoH, baked ? 0 : _rotation);
        var crop = _cropping ? FullCrop : baked ? _cropBaked : _crop; // F4: só a parte cortada
        return Fit(_clientW, _clientH, CropSize(bw, crop.w), CropSize(bh, crop.h));
    }

    // Home / End: espelha a imagem da tela na horizontal / vertical (vale também para fotos e com o giro).
    private void Flip(bool horizontal)
    {
        bool on;
        lock (_lock)
        {
            on = horizontal ? (_flipH = !_flipH) : (_flipV = !_flipV);
            // O corte (na imagem da tela) espelha junto: continua na mesma parte da foto.
            if (horizontal) _crop.x = 1 - _crop.x - _crop.w;
            else _crop.y = 1 - _crop.y - _crop.h;
            _cropSel = _crop;
            _dirty = true;
        }
        _renderWake.Set();
        Notify($"{(horizontal ? "Espelho horizontal" : "Espelho vertical")}: {(on ? "ligado" : "desligado")}");
    }

    private void Rotate(int degrees)
    {
        lock (_lock)
        {
            _rotation = ((_rotation + degrees) % 360 + 360) % 360;
            _crop = _cropSel = FullCrop; // o corte era da imagem no giro anterior
            _zoom = 1;
            _zoomX = _zoomY = 0;
            _dirty = true;
        }
        _renderWake.Set();
        Notify(_rotation == 0 ? "Sem giro" : $"Girado {(_rotation > 180 ? _rotation - 360 : _rotation)}°");
    }

    // Retângulo do vídeo ajustado à janela (sem zoom). Com _lock.
    private (double x, double y, double w, double h) Fit(int cw, int ch, int vw, int vh)
    {
        if (vw <= 0 || vh <= 0) return (0, 0, cw, ch);
        double scale = Math.Min((double)cw / vw, (double)ch / vh);
        double w = vw * scale, h = vh * scale;
        return ((cw - w) / 2, (ch - h) / 2, w, h);
    }

    // Parte visível da imagem ampliada: onde desenhar na janela e que parte do vídeo (frações). O corte (F4, frações da
    // imagem girada) é a imagem que se encaixa na janela. Com _lock.
    private (VideoView, Native.RECT) Layout(int cw, int ch, int vw, int vh, int rotation, (double x, double y, double w, double h) crop)
    {
        (vw, vh) = Box(vw, vh, rotation);
        var (rx, ry, rw, rh) = Fit(cw, ch, CropSize(vw, crop.w), CropSize(vh, crop.h));
        double iw = rw * _zoom, ih = rh * _zoom, ix = rx + _zoomX * rw, iy = ry + _zoomY * rh;
        int left = (int)Math.Round(Math.Max(ix, 0)), top = (int)Math.Round(Math.Max(iy, 0));
        int right = (int)Math.Round(Math.Min(ix + iw, cw)), bottom = (int)Math.Round(Math.Min(iy + ih, ch));
        var output = new Native.RECT { Left = left, Top = top, Right = Math.Max(left, right), Bottom = Math.Max(top, bottom) };
        var view = new VideoView(default, vw, vh, (float)(crop.x + (left - ix) / iw * crop.w), (float)(crop.y + (top - iy) / ih * crop.h),
            (float)(output.Width / iw * crop.w), (float)(output.Height / ih * crop.h));
        return (view, output);
    }

    // Posiciona a imagem ampliada sem buracos: maior que a janela num eixo → cobre a janela; menor → dentro dela.
    private void PlaceImage(double ix, double iy)
    {
        var (rx, ry, rw, rh) = FitDisplay();
        if (_zoom <= 1.0001)
        {
            _zoom = 1;
            _zoomX = _zoomY = 0;
            return;
        }
        static double Axis(double pos, double size, double window) =>
            size >= window ? Math.Clamp(pos, window - size, 0) : Math.Clamp(pos, 0, window - size);
        ix = Axis(ix, rw * _zoom, _clientW);
        iy = Axis(iy, rh * _zoom, _clientH);
        _zoomX = (ix - rx) / rw;
        _zoomY = (iy - ry) / rh;
    }

    private void ZoomAt(int x, int y, double factor)
    {
        lock (_lock)
        {
            if ((_file is null && !_image) || _cropping) return; // no corte (F4), a imagem fica inteira
            var (rx, ry, rw, rh) = FitDisplay();
            double ix = rx + _zoomX * rw, iy = ry + _zoomY * rh;
            double zoom = Math.Clamp(_zoom * factor, 1, MaxZoom);
            double k = zoom / _zoom;
            _zoom = zoom;
            PlaceImage(x - (x - ix) * k, y - (y - iy) * k); // o ponto sob o mouse fica parado
            _dirty = true;
        }
        _renderWake.Set();
        Notify(_zoom <= 1 ? "Sem zoom" : $"Zoom {Dot(_zoom, "0.0")}x");
    }

    private void Pan(int dx, int dy)
    {
        lock (_lock)
        {
            if (_zoom <= 1) return;
            var (rx, ry, rw, rh) = FitDisplay();
            PlaceImage(rx + _zoomX * rw + dx, ry + _zoomY * rh + dy);
            _dirty = true;
        }
        _renderWake.Set();
    }

    private void ResetZoom()
    {
        lock (_lock)
        {
            _zoom = 1;
            _zoomX = _zoomY = 0;
            _dirty = true;
        }
        _renderWake.Set();
        Notify("Sem zoom");
    }

    // ---------- Janela (thread principal) ----------

    private static IntPtr Dispatch(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        Player? player;
        lock (Windows) Windows.TryGetValue(hwnd, out player);
        player ??= _creating;
        return player is not null ? player.WindowProc(hwnd, msg, wParam, lParam) : DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (FrameMessage(hwnd, msg, wParam, lParam) is { } handled) return handled; // moldura do navegador (PlayerWebFrame)
        switch (msg)
        {
            case 0x0006: // WM_ACTIVATE: WA_CLICKACTIVE = ativada por um clique (o clique chega logo depois)
                if ((wParam.ToInt64() & 0xffff) == 2) _clickActivatedAt = Environment.TickCount64;
                break;
            case WM_SIZE:
                lock (_lock)
                {
                    _clientW = LowWord(lParam) & 0xffff;
                    _clientH = HighWord(lParam) & 0xffff;
                    _resized = _dirty = true;
                }
                LayoutWeb();
                _renderWake.Set();
                return 0;
            case WM_RUN:
                RunUiQueue();
                return 0;
            case WM_CTLCOLOREDIT when lParam == _address && _address != IntPtr.Zero:
                return AddressColors(wParam);
            case WM_ERASEBKGND:
                return 1;
            case WM_SETCURSOR when ((long)lParam & 0xffff) == 1 /* HTCLIENT */ && (CropCursor() || AnnotateCursor()): // alças do corte (F4), mira para desenhar (F3)
                return 1;
            case WM_KEYDOWN or WM_SYSKEYDOWN:
                if (OnKey((int)wParam)) return 0;
                break;
            case WM_MOUSEWHEEL:
            {
                var point = new Native.POINT { X = LowWord(lParam), Y = HighWord(lParam) };
                ScreenToClient(hwnd, ref point);
                if (WebWheel(point.X, point.Y, HighWord(wParam))) return 0; // faixas do navegador (rola as abas)
                if (!OnListWheel(point.X, point.Y, HighWord(wParam))) ZoomAt(point.X, point.Y, Math.Pow(1.2, HighWord(wParam) / 120.0));
                return 0;
            }
            case WM_LBUTTONDOWN:
                OnMouseDown(LowWord(lParam), HighWord(lParam), doubleClick: false);
                return 0;
            case 0x020B: // WM_XBUTTONDOWN: botões laterais do mouse
            {
                bool listOpen;
                lock (_lock) listOpen = _listOpen;
                if (!listOpen) break;
                Navigate(HighWord(wParam) == 1 ? -1 : 1);
                return 1;
            }
            case WM_LBUTTONDBLCLK:
                OnMouseDown(LowWord(lParam), HighWord(lParam), doubleClick: true);
                return 0;
            case WM_MOUSEMOVE:
                OnMouseMove(LowWord(lParam), HighWord(lParam));
                return 0;
            case WM_LBUTTONUP:
                OnMouseUp();
                return 0;
            case WM_MOUSELEAVE:
                OnMouseLeave();
                return 0;
            case WM_MBUTTONDOWN:
                if (WebMode) WebMiddleClick(LowWord(lParam), HighWord(lParam));
                else ResetZoom();
                return 0;
            case 0x0205: // WM_RBUTTONUP: menus das abas e dos favoritos; no player, o menu da imagem
                if (WebMode) WebRightClick(LowWord(lParam), HighWord(lParam));
                else PlayerRightClick(LowWord(lParam), HighWord(lParam));
                return 0;
            case WM_DROPFILES:
            {
                var buffer = new char[2048];
                uint length = DragQueryFile(wParam, 0, buffer, (uint)buffer.Length);
                DragFinish(wParam);
                if (length > 0) Open(new string(buffer, 0, (int)length));
                return 0;
            }
            case WM_TIMER:
                AutoHidePanel();
                UpdateTitle();
                LogDiagnostics();
                SyncAudio();
                WebTimer(); // navegador: dica das abas, abas dormindo
                return 0;
            case SingleInstance.WM_COPYDATA: // outra cópia do MK Vision (vídeo aberto pelo Explorer) entregou o vídeo
            {
                string? path = SingleInstance.Receive(lParam);
                if (path is null) break;
                if (path != "" && File.Exists(path)) Open(path);
                BringToFront();
                return 1;
            }
            case WM_CLOSE:
                if (ClearBeforeClose()) return 0; // apaga o histórico interno e o cache; depois fecha
                Dispose();
                return 0;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private bool OnKey(int key)
    {
        if (key == 0x71) // F2: player ↔ navegador
        {
            ToggleWeb();
            return true;
        }
        if (WebMode) return false; // no navegador, as teclas são do site
        if (key == 0x73) // F4: cortar
        {
            ToggleCrop();
            return true;
        }
        if (CropKey(key)) return true; // no corte: Enter aceita, Esc cancela, o resto espera
        if (key == 0x72) // F3: anotações (pincel e retângulo)
        {
            ToggleAnnotate();
            return true;
        }
        bool annotating;
        lock (_lock) annotating = _annotating;
        if (annotating && AnnotateKey(key)) return true; // Esc fecha, Ctrl+Z desfaz
        bool shift = KeyDown(VK_SHIFT), ctrl = KeyDown(VK_CONTROL);
        switch (key)
        {
            case VK_SPACE: TogglePlay(); break;
            case VK_LEFT when shift: StepFrame(-1); break;
            case VK_RIGHT when shift: StepFrame(1); break;
            case VK_LEFT or VK_RIGHT:
            {
                double target = Math.Clamp(CurrentFileTime() + (key == VK_RIGHT ? 5 : -5), 0, Math.Max(0, _duration));
                SeekTo(target);
                Notify($"{(key == VK_RIGHT ? "+5 s" : "−5 s")}  ·  {ClockText(target)}");
                break;
            }
            case VK_UP: SetSpeed(_speed + 0.1); break;
            case VK_DOWN: SetSpeed(_speed - 0.1); break;
            case VK_BACK: SetSpeed(1); break;
            case VK_HOME when !ctrl: Flip(horizontal: true); break;
            case 0x23: Flip(horizontal: false); break; // End
            case VK_HOME:
                SeekTo(0);
                Notify("Início");
                break;
            case VK_RETURN or 'F': ToggleFullscreen(); break;
            case VK_ESCAPE:
                if (_fullscreen) ToggleFullscreen();
                else if (_fromWeb) ShowWeb(true); // veio de um site: volta para ele
                break;
            case 'Z': ResetZoom(); break;
            case 'I': EnhanceShown(); break; // melhora com a IA de fotos a base da tela (original ou a do ComfyUI)
            case 'U': ToggleAi(); break;     // liga/desliga a melhoria do I
            case 'Y': ToggleTone(); break;   // luz e cor (ToneAi) na versão da tela; de novo: liga/desliga
            case 'T': ToggleComfy(); break;  // base: original ↔ a do ComfyUI
            case 'G': ComfyKey(cancel: shift); return true; // quadro/foto → ComfyUI (Shift+G cancela)
            case 'D':
                _settings.SharpenOff = !_settings.SharpenOff;
                Notify(_settings.SharpenOff ? "Nitidez do player: desligada (D liga)" : "Nitidez do player: ligada");
                break;
            case 'R':
                _settings.RtxVideo = !_settings.RtxVideo;
                if (_settings.RtxVideo) _pipeline.RtxRecheck(); // pode ter sido ligado no driver agora
                OnSettingsChanged();
                Notify(_settings.RtxVideo ? "RTX Video: ligado" : "RTX Video: desligado (nossa IA)");
                break;
            case 'S' when ctrl: SavePhotoAi(); break; // a imagem da tela (versão da IA/ComfyUI ou o quadro) → arquivo
            case 'S' when shift: SaveVideo(); break;  // o vídeo aberto inteiro → arquivo
            case 'S':
                _prefs.Smooth = !_prefs.Smooth;
                Notify(_prefs.Smooth ? "Câmera lenta suave: ligada" : "Câmera lenta suave: desligada");
                break;
            case 'P':
                Notify($"Perfil: {_prefs.NextProfile().Name}");
                break;
            case 'M':
                if (!RifeHost.HasTrained)
                {
                    Notify("Ainda não há IA treinada com os seus vídeos");
                    break;
                }
                _prefs.TrainedModel = !_prefs.TrainedModel;
                RifeHost.Use(_prefs.TrainedModel);
                Notify(_prefs.TrainedModel ? "IA de câmera lenta: treinada com os seus vídeos" : "IA de câmera lenta: original");
                lock (_lock) foreach (var frame in _queue) frame.Analyzed = false; // refaz com o modelo novo
                _prefs.Save();
                _analysisWake.Set();
                break;
            case 'N':
                _prefs.Crisp = !_prefs.Crisp;
                Notify(_prefs.Crisp ? "Câmera lenta nítida: ligada" : "Câmera lenta nítida: desligada");
                break;
            case 'E':
                _prefs.Ensemble = !_prefs.Ensemble;
                Notify(_prefs.Ensemble ? "IA de quadros em conjunto: ligada" : "IA de quadros em conjunto: desligada");
                break;
            case 'B':
                _settings.Bypass = !_settings.Bypass;
                _bypassForAi = false; // mexeu no B: o fim da IA de fotos não religa por conta própria
                Notify(_settings.Bypass ? "Comparando: sem processamento" : "Processamento ligado");
                break;
            case VK_ADD or VK_OEM_PLUS:
                _settings.Strength = Math.Min(4, _settings.Strength + 0.25f);
                Notify($"Força {_settings.Strength * 100:0}%");
                break;
            case VK_SUBTRACT or VK_OEM_MINUS:
                _settings.Strength = Math.Max(0, _settings.Strength - 0.25f);
                Notify($"Força {_settings.Strength * 100:0}%");
                break;
            case 'A' or VK_TAB:
                _panel?.Toggle();
                return true;
            case 'L':
                lock (_lock) _loop = !_loop;
                Notify(_loop ? "Repetir: ligado" : "Repetir: desligado");
                break;
            case 0x21: OpenSibling(-1); return true; // Page Up
            case 0x22: OpenSibling(1); return true;  // Page Down
            case 0x2D: Rotate(-45); break;           // Insert
            case 0x2E: Rotate(45); break;            // Delete
            case 'O' when ctrl: ShowOpenDialog(); break;
            case >= '1' and <= '4' when ctrl && shift:
                SetListMode(key - '0'); // tamanho dos ícones da lista, como no Explorer
                return true;
            case >= '1' and <= '5':
                _settings.Power = key - '0';
                Notify($"Potência {_settings.Power} · {PowerNames[_settings.Power]}");
                break;
            default:
                return false;
        }
        _panel?.Refresh();
        OnSettingsChanged();
        return true;
    }

    // Números com ponto (0.3x), como na extensão — o formato brasileiro do Windows usaria vírgula.
    private static string Dot(double value, string format) => value.ToString(format, System.Globalization.CultureInfo.InvariantCulture);

    private static readonly string[] PowerNames = ["", "só nitidez", "IA rápida", "IA qualidade", "IA qualidade", "IA dupla (pesada)"];

    private double CurrentFileTime()
    {
        lock (_lock) return _shownFileTime;
    }

    // Tela cheia sem borda (a janela vira do tamanho do monitor) e de volta.
    private void ToggleFullscreen(bool notify = true)
    {
        if (!_fullscreen)
        {
            GetWindowRect(Hwnd, out _windowedRect);
            var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(MonitorFromWindow(Hwnd, MONITOR_DEFAULTTONEAREST), ref info);
            SetWindowLongPtr(Hwnd, GWL_STYLE, (IntPtr)(long)(WS_POPUP | WS_VISIBLE | WS_CLIPCHILDREN));
            var m = info.rcMonitor;
            Native.SetWindowPos(Hwnd, IntPtr.Zero, m.Left, m.Top, m.Width, m.Height, SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        }
        else
        {
            SetWindowLongPtr(Hwnd, GWL_STYLE, (IntPtr)(long)(WS_OVERLAPPEDWINDOW | WS_VISIBLE | WS_CLIPCHILDREN));
            var r = _windowedRect;
            Native.SetWindowPos(Hwnd, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height, SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        }
        _fullscreen = !_fullscreen;
        RefreshFrame(); // no navegador, a janela volta sem a barra de título
        if (notify) Notify(_fullscreen ? "Tela cheia" : "Janela");
    }

    private void UpdateTitle()
    {
        string title;
        lock (_lock)
        {
            if (_webMode)
                title = "MK Vision — Navegador" + (_webTitle.Length > 0 ? " — " + _webTitle : "");
            else if (_image)
                title = $"MK Vision — {_fileName} — foto {_videoW}x{_videoH}" + (_zoom > 1 ? $" — zoom {Dot(_zoom, "0.0")}x" : "") +
                        (_rotation != 0 ? $" — girado {(_rotation > 180 ? _rotation - 360 : _rotation)}°" : "") +
                        (_folderFiles.Length > 1 && _folderIndex >= 0 ? $" — {_folderIndex + 1}/{_folderFiles.Length} da pasta" : "") +
                        (_settings.Bypass ? " — COMPARANDO: sem processamento" : $" — {_prefs.ProfileName}, força {_settings.Strength * 100:0}%, potência {_settings.Power}");
            else if (_file is null)
                title = _error != "" ? $"MK Vision — não abriu: {_error}" : "MK Vision";
            else
                title = $"MK Vision — {_fileName} — {TimeText(_shownFileTime)} / {TimeText(_duration)} — " +
                        (_playing ? "" : "pausado — ") + $"{Dot(_speed, "0.0#")}x" + (_zoom > 1 ? $" — zoom {Dot(_zoom, "0.0")}x" : "") +
                        (_rotation != 0 ? $" — girado {(_rotation > 180 ? _rotation - 360 : _rotation)}°" : "") +
                        (_folderFiles.Length > 1 && _folderIndex >= 0 ? $" — {_folderIndex + 1}/{_folderFiles.Length} da pasta" : "") +
                        (_loop ? "" : " — sem repetir") +
                        (_settings.Bypass ? " — COMPARANDO: sem processamento" : $" — {_prefs.ProfileName}, força {_settings.Strength * 100:0}%, potência {_settings.Power}") +
                        SlowTitle();
        }
        if (title == _lastTitle) return;
        _lastTitle = title;
        SetWindowText(Hwnd, title);
    }

    // Ajuste mudou (painel ou tecla): redesenha, liga/desliga a câmera lenta e guarda para a próxima vez.
    private bool _analysisEnsemble, _analysisTrained = true;
    private int _photoAiModel = -1;

    private void OnSettingsChanged()
    {
        PhotoDetailChanged(); // realce de textura / grão (só refaz se mudou)
        if (_photoAiModel != _prefs.PhotoAi)
        {
            bool first = _photoAiModel < 0;
            _photoAiModel = _prefs.PhotoAi;
            if (!first) PhotoAiChanged(); // trocou o modelo: refaz a foto aberta
        }
        lock (_lock)
        {
            _smooth = _prefs.Smooth;
            _settingsVersion++;
            if (_analysisEnsemble != _prefs.Ensemble || _analysisTrained != _prefs.TrainedModel)
            {
                (_analysisEnsemble, _analysisTrained) = (_prefs.Ensemble, _prefs.TrainedModel);
                foreach (var frame in _queue) frame.Analyzed = false; // a IA refaz os pares com o novo jeito
            }
            _dirty = true;
        }
        _renderWake.Set();
        _analysisWake.Set();
        _prefs.Save();
        UpdateTitle();
    }

    // ---------- Câmera lenta (thread própria) ----------

    // Primeiro vídeo: a IA de câmera lenta começa a carregar (em segundo plano; depois, nada).
    private void StartSlowAi()
    {
        if (RifeHost.Started) return;
        if (!NvidiaPack.Installed) return; // sem as IAs da NVIDIA (PlayerNvidia): o vídeo fica lento sem quadros novos
        RifeHost.Start(_prefs.TrainedModel);
        RifeHost.Use(_prefs.TrainedModel);
    }

    // Com _lock.
    private bool SlowActive() => _smooth && _speed < 0.999 && _file is not null && RifeHost.Instance is not null;

    // Situação do RTX Video, para o painel de ajustes (aba Ampliação).
    private string ZoomStatus()
    {
        if (!_settings.RtxVideo) return "Desligado: a ampliação é feita pela nossa IA (abaixo).";
        return _pipeline.RtxWorks switch
        {
            true => "Ativo: amplia vídeos e fotos sem zoom (com zoom, usa a nossa IA).",
            false => "Sem efeito: ligue \"Super Resolution\" no app NVIDIA (Sistema → Vídeo → Aprimoramento de vídeo RTX), ou a placa não é NVIDIA RTX.",
            null => "Ligado: entra no primeiro vídeo ou foto ampliado."
        };
    }

    // Situação da câmera lenta, para o painel de ajustes (aba Câmera lenta).
    private string PanelStatus()
    {
        lock (_lock)
        {
            if (!_smooth) return "Desligada: abaixo de 1x o vídeo fica só mais lento, sem quadros novos.";
            if (RifeHost.Instance is null)
                return NvidiaPack.Installed ? "Carregando a IA de quadros..." : "Precisa das IAs da NVIDIA (botão direito na imagem → Baixar as IAs da NVIDIA).";
            string ai = RifeHost.UsingTrained ? "IA treinada" : "IA original";
            if (_file is null || _speed >= 0.999) return $"Pronta ({ai}): entra quando a velocidade fica abaixo de 1x (seta para baixo).";
            return $"Agora: {Dot(_speed, "0.##")}x · {ai}" + (_slowInfo != "" ? $" · {_slowInfo}" : "");
        }
    }

    // Com _lock.
    private string SlowTitle()
    {
        if (!_smooth || _speed >= 0.999 || _file is null) return "";
        if (RifeHost.Instance is null) return " — câmera lenta: carregando a IA";
        string late = _statSlowFrames > 0 && _statNoMotion * 20 > _statSlowFrames ? " (IA atrasada)" : "";
        _statNoMotion = _statSlowFrames = 0;
        return $" — câmera lenta IA{(_slowInfo != "" ? ": " + _slowInfo : "")}{late}";
    }

    // Analisa os pares da fila à frente da tela, do mais próximo para o mais distante.
    private void AnalysisLoop()
    {
        while (_running)
        {
            Frame? a = null, b = null;
            long serialA = 0, serialB = 0;
            double speed = 1;
            bool reload;
            lock (_lock)
            {
                // Descarregada (tecla G libera a placa para o ComfyUI): carrega de novo quando o vídeo toca em câmera lenta.
                reload = _smooth && _speed < 0.999 && _file is not null && _playing && !RifeHost.Started;
                if (SlowActive())
                {
                    speed = _speed;
                    for (int i = 0; i + 1 < _queue.Count; i++)
                    {
                        var f = _queue[i];
                        var n = _queue[i + 1];
                        if (f.Analyzed) continue; // já tem movimento (ou não dá: falhou, emenda, lacuna)
                        if (n.FileTime <= f.FileTime || n.Time - f.Time > 1.0 || n.Width != f.Width || n.Height != f.Height)
                        {
                            f.Analyzed = true; // emenda da repetição ou lacuna: sem IA
                            continue;
                        }
                        (a, b, serialA, serialB) = (f, n, f.Serial, n.Serial);
                        break;
                    }
                }
            }
            if (reload) StartSlowAi();
            if (a is null || b is null)
            {
                _analysisWake.WaitOne(30);
                continue;
            }
            try
            {
                AnalyzePair(a, b, serialA, serialB, speed);
            }
            catch (Exception error)
            {
                Log.Write($"Player: falha na IA de quadros — {error.Message}");
                lock (_lock) if (a.Serial == serialA) a.Analyzed = true;
                Thread.Sleep(200);
            }
        }
    }

    private void AnalyzePair(Frame a, Frame b, long serialA, long serialB, double speed)
    {
        var rife = RifeHost.Instance!;
        // Tempo de tela de um par: tudo o que a IA pode gastar com ele (ela trabalha à frente da tela).
        double pairMs = (b.Time - a.Time) / speed * 1000;
        double analysisStart = Now;
        var (budget, maxSteps) = _prefs.SlowBudget; // desempenho: quanto da placa a IA de quadros pode usar
        bool ensemble = _prefs.Ensemble;
        int runs = ensemble ? 2 : 1; // rodadas da IA por momento analisado
        // Atrasada (logo depois de abrir, pular, mudar a velocidade ou um ajuste): o par já está na tela, ou
        // entra antes de uma análise completa ficar pronta. Sem análise a tela segura o quadro e depois pula —
        // partes da imagem "somem e aparecem" em outro lugar. Então faz uma análise rápida (um momento,
        // tamanho menor, sem conjunto) que fica pronta a tempo; a fila à frente volta a ter a completa.
        double untilMs;
        lock (_lock) untilMs = (a.Time - MediaNow()) / speed * 1000;
        bool quick = untilMs < pairMs * budget;
        int aw, ah;
        if (quick)
        {
            (aw, ah) = QuickSize(a.Width, a.Height, Math.Clamp(untilMs, Math.Min(25, pairMs * budget), pairMs * budget));
            ensemble = false;
            runs = 1;
            maxSteps = 1;
        }
        else (aw, ah) = AnalysisSize(a.Width, a.Height, pairMs * budget / runs);
        PrepareFrameInput(a, serialA, aw, ah);
        PrepareFrameInput(b, serialB, aw, ah);
        bool Current() { lock (_lock) return a.Serial == serialA && b.Serial == serialB && a.RgbSerial == serialA && b.RgbSerial == serialB; }
        if (!Current()) return;
        double megapixels = aw * ah / 1e6;
        int count = Math.Min(maxSteps, (int)(pairMs * budget / (_fi.MsPerMegapixel * megapixels * runs))); // momentos que cabem
        var motion = _fi.Analyze(rife, a.Rgb!, b.Rgb!, aw, ah, count, Current, ensemble);
        if (motion is null) return;
        motion.Key = b;
        lock (_gpuLock) // a tela pode estar desenhando com o movimento antigo deste quadro
        lock (_lock)
        {
            if (a.Serial == serialA && b.Serial == serialB)
            {
                a.Motion?.Dispose();
                a.Motion = motion;
                a.Analyzed = true;
                if (!quick) _slowInfo = $"{aw}x{ah}, {motion.Steps.Length} análise(s) por par" + (ensemble ? " em conjunto" : "") + (RifeHost.UsingTrained ? ", IA treinada" : "");
                else _diagQuick++;
                _diagAnalysisMs += (Now - analysisStart) * 1000;
                _diagPairMs += pairMs;
                _diagPairs++;
                motion = null;
            }
        }
        motion?.Dispose();
        _renderWake.Set();
    }

    // A cada ~3 s com a câmera lenta ativa: quadros de tela, pausas (> 40 ms entre um e outro), quadros sem a
    // análise pronta e quanto do tempo de cada par a IA gastou.
    private void LogDiagnostics()
    {
        string? line = null;
        lock (_lock)
        {
            double now = Now;
            if (_diagStart == 0 || !SlowActive() || !_playing) { _diagStart = now; _diagLastPresent = 0; _diagFrames = _diagGaps = _diagHeld = _diagSlow = _diagPairs = _diagQuick = 0; _diagMaxGap = _diagDrawMs = _diagAnalysisMs = _diagPairMs = 0; return; }
            if (now - _diagStart < 3) return;
            double seconds = now - _diagStart;
            line = $"Câmera lenta: {_diagFrames / seconds:0} quadros/s, pausas {_diagGaps} (maior {_diagMaxGap:0} ms), desenho até {_diagDrawMs:0} ms, " +
                   $"sem análise {_diagHeld} de {_diagSlow}, análises rápidas {_diagQuick}, IA {(_diagPairs > 0 ? _diagAnalysisMs / _diagPairs : 0):0} ms por par de {(_diagPairs > 0 ? _diagPairMs / _diagPairs : 0):0} ms ({_slowInfo}), " +
                   $"{_fi.MsPerMegapixel:0} ms/MP";
            _diagStart = now; _diagFrames = _diagGaps = _diagHeld = _diagSlow = _diagPairs = _diagQuick = 0; _diagMaxGap = _diagDrawMs = _diagAnalysisMs = _diagPairMs = 0;
        }
        Log.Write(line);
    }

    // Análise rápida (atrasada): o tamanho em que uma rodada da IA leva ~"ms".
    private (int, int) QuickSize(int w, int h, double ms)
    {
        double scale = Math.Min(1, Math.Min((double)MaxAnalysisW / w, (double)MaxAnalysisH / h));
        scale = Math.Min(scale, Math.Sqrt(ms / _fi.MsPerMegapixel * 1e6 / ((double)w * h)));
        scale = Math.Max(scale, 320.0 / Math.Max(w, h));
        return (Math.Max(2, (int)(w * scale / 2) * 2), Math.Max(2, (int)(h * scale / 2) * 2));
    }

    // Maior tamanho que cabe no tempo dado (parte do tempo de um par), até o limite do motor; muda só com folga (>15%).
    private (int, int) AnalysisSize(int w, int h, double budgetMs)
    {
        double scale = Math.Min(1, Math.Min((double)MaxAnalysisW / w, (double)MaxAnalysisH / h));
        double affordable = budgetMs / _fi.MsPerMegapixel; // megapixels que cabem numa análise
        double megapixels = w * scale * h * scale / 1e6;
        if (megapixels > affordable) scale *= Math.Sqrt(affordable / megapixels);
        scale = Math.Max(scale, 320.0 / Math.Max(w, h));
        int aw = Math.Max(2, (int)(w * scale / 2) * 2), ah = Math.Max(2, (int)(h * scale / 2) * 2);
        var (pw, ph) = _analysisSize;
        if (pw > 0 && Math.Abs((double)aw / pw - 1) < 0.15 && (double)pw / w <= 1.0001 && (double)ph / h <= 1.0001 && Math.Abs((double)pw / ph - (double)w / h) < 0.02)
            return (pw, ph);
        _analysisSize = (aw, ah);
        return (aw, ah);
    }

    // Entrada da IA de um quadro (reaproveitada se já estiver pronta no mesmo tamanho).
    private void PrepareFrameInput(Frame frame, long serial, int aw, int ah)
    {
        float[] rgb;
        lock (_lock)
        {
            if (frame.Serial != serial) return;
            if (frame.RgbSerial == serial && frame.RgbW == aw && frame.RgbH == ah) return;
            if (frame.Rgb is null || frame.Rgb.Length != 3 * aw * ah) frame.Rgb = new float[3 * aw * ah];
            rgb = frame.Rgb;
            frame.RgbSerial = -1;
        }
        _fi.PrepareInput(frame.View, aw, ah, rgb);
        lock (_lock)
        {
            if (frame.Serial != serial) return; // a textura virou outro quadro no meio
            (frame.RgbW, frame.RgbH, frame.RgbSerial) = (aw, ah, serial);
        }
    }

    private static string TimeText(double seconds) =>
        seconds <= 0 ? "0:00" : TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");

    // Thread principal (ao fechar a janela ou o app).
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;
        _decodeWake.Set();
        _renderWake.Set();
        _analysisWake.Set();
        _renderThread.Join(2000);
        _decodeThread.Join(2000);
        _analysisThread.Join(5000);
        lock (_gpuLock)
        {
            foreach (var frame in _queue.Concat(_pool))
            {
                frame.Motion?.Dispose();
                frame.Processed?.Dispose();
                frame.View.Dispose();
                frame.Texture.Dispose();
            }
            _queue.Clear();
            _pool.Clear();
            _backTarget?.Dispose();
            _backBuffer?.Dispose();
            _swapChain?.Dispose();
            _output.Dispose();
            _snapshot?.Dispose();
            DisposeInterface();
            DisposeWeb();
            _interp.Dispose();
            _rotated.Dispose();
            foreach (var small in _imageSmall) small.Dispose();
            _rotateShader.Dispose();
            _fi.Dispose();
            _pipeline.Dispose();
        }
        _file?.Dispose();
        _prefs.Save();
        _panel?.Dispose();
        lock (Windows) Windows.Remove(Hwnd);
        Native.DestroyWindow(Hwnd);
        Log.Write("Player fechado.");
        Closed?.Invoke();
    }
}
