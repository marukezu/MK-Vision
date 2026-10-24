namespace MKVision.Desktop;

// O pacote de IA da NVIDIA no player (NvidiaPack): sem ele, a IA de fotos (I) pergunta se pode baixar (numa placa
// NVIDIA) ou avisa que precisa de uma (nas outras); a câmera lenta com IA só não liga, sem perguntar nada. O download
// roda em segundo plano, com a porcentagem nos avisos da tela; pronto, a câmera lenta com IA já liga no vídeo aberto.
internal sealed partial class Player
{
    private (bool nvidia, string name)? _gpuInfo;
    private (long done, long total) _packProgress;

    private (bool nvidia, string name) GpuInfo() => _gpuInfo ??= NvidiaPack.Gpu();

    // Antes de uma IA da placa: true = o pacote não está pronto (e a pessoa já foi avisada ou perguntada).
    private bool NeedNvidiaPack()
    {
        if (NvidiaPack.Installed) return false;
        if (NvidiaPack.Downloading)
        {
            Notify(PackText());
            return true;
        }
        var (nvidia, _) = GpuInfo();
        if (!nvidia)
        {
            Notify("As IAs do MK Vision precisam de uma placa NVIDIA (de preferência RTX)");
            return true;
        }
        if (TestPackAnswer ?? MessageBoxW(Hwnd, "As IAs do MK Vision (IA de fotos, câmera lenta com IA e luz e cor na placa) precisam das bibliotecas " +
                "da NVIDIA: CUDA, cuDNN e TensorRT.\n\nSão cerca de 3 GB, baixados uma vez só, direto da NVIDIA. O app continua funcionando " +
                "enquanto baixa.\n\nBaixar agora?", "MK Vision — IAs da NVIDIA", 0x4 | 0x20 /* SIM/NÃO, PERGUNTA */) == 6)
            StartPackDownload();
        return true;
    }

    private string PackText()
    {
        var (done, total) = _packProgress;
        return total > 0 ? $"Baixando as IAs da NVIDIA: {done * 100 / total}% ({done / 1073741824.0:0.0} de {total / 1073741824.0:0.0} GB)"
                         : "Baixando as IAs da NVIDIA...";
    }

    private void StartPackDownload()
    {
        if (NvidiaPack.Downloading) return;
        Log.Write("IA da NVIDIA: baixando o pacote.");
        Notify("Baixando as IAs da NVIDIA...");
        _ = Task.Run(async () =>
        {
            try
            {
                await NvidiaPack.Download((done, total) =>
                {
                    _packProgress = (done, total);
                    Notify(PackText());
                }, CancellationToken.None);
                Log.Write("IA da NVIDIA: pacote completo.");
                Notify("IAs da NVIDIA prontas: o I, a câmera lenta com IA e o Y já usam a placa");
                PostUi(() =>
                {
                    bool video;
                    lock (_lock) video = _file is not null;
                    if (video) StartSlowAi(); // com um vídeo aberto, a câmera lenta com IA já liga
                });
            }
            catch (Exception error)
            {
                Log.Write($"IA da NVIDIA: o download falhou — {error.Message}");
                Notify("Não consegui baixar as IAs da NVIDIA: " + error.Message.Split('\n')[0]);
            }
        });
    }

    // Para o menu do botão direito: o item de baixar (ou o andamento), só numa placa NVIDIA sem o pacote.
    private MenuEntry? PackMenuEntry()
    {
        if (NvidiaPack.Installed || !GpuInfo().nvidia) return null;
        return NvidiaPack.Downloading ? new MenuEntry(PackText(), () => Notify(PackText())) : new MenuEntry("Baixar as IAs da NVIDIA (3 GB)", () => NeedNvidiaPack());
    }

    // Autoteste: a resposta da pergunta (sem a janela).
    public bool? TestPackAnswer;
}
