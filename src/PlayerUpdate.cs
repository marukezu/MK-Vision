namespace MKVision.Desktop;

// Atualização no player (Updater): uns segundos depois de abrir, pergunta ao GitHub se há versão nova; havendo, avisa
// na tela e o menu do botão direito (e o Arquivo) ganha "Atualizar para a versão X". Atualizar: confirma, baixa (com a
// porcentagem nos avisos), fecha e o app abre de novo já atualizado.
internal sealed partial class Player
{
    private Updater.Release? _update;
    private bool _updating;

    private void ScheduleUpdateCheck()
    {
        if (!Updater.Enabled) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(Environment.GetEnvironmentVariable("MKV_UPDATE_DIR") is null ? 8 : 0));
            try
            {
                if (await Updater.Check(CancellationToken.None) is not { } release) return;
                _update = release;
                Log.Write($"Atualização: versão {release.Version} disponível (esta: {Updater.Current}).");
                Notify($"Versão nova do MK Vision: {release.Version} · botão direito na imagem → Atualizar");
            }
            catch (Exception error) { Log.Write($"Atualização: não consegui consultar o GitHub — {error.Message}"); }
        });
    }

    // O item dos menus: null = em dia.
    private string? UpdateMenuText() => _update is { } u ? (_updating ? $"Baixando a versão {u.Version}..." : $"Atualizar para a versão {u.Version}") : null;

    private void StartUpdate()
    {
        if (_update is not { } release || _updating) return;
        string notes = release.Notes.Trim();
        if (notes.Length > 600) notes = notes[..600].TrimEnd() + "…";
        if (!(TestUpdateAnswer ?? MessageBoxW(Hwnd, $"Atualizar o MK Vision da versão {Updater.Current} para a {release.Version}?\n\n" +
                $"São {release.Size / 1048576.0:0} MB. Ao terminar de baixar, o app fecha e abre de novo já atualizado (as configurações, " +
                "os favoritos e o resto continuam).\n\n" + (notes.Length > 0 ? "Novidades:\n" + notes : ""), "MK Vision — atualização",
                0x4 | 0x40 /* SIM/NÃO, INFORMAÇÃO */) == 6))
            return;
        _updating = true;
        Notify($"Baixando a versão {release.Version}...");
        _ = Task.Run(async () =>
        {
            try
            {
                string folder = await Updater.Download(release, (done, total) =>
                    Notify($"Baixando a versão {release.Version}: {(total > 0 ? done * 100 / total : 0)}%"), CancellationToken.None);
                Updater.ApplyAfterExit(folder);
                Notify($"Versão {release.Version} pronta: o MK Vision vai fechar e abrir de novo");
                await Task.Delay(1500);
                PlayerNative.PostMessage(Hwnd, PlayerNative.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
            catch (Exception error)
            {
                _updating = false;
                Log.Write($"Atualização: falhou — {error.Message}");
                Notify("Não consegui atualizar: " + error.Message.Split('\n')[0]);
            }
        });
    }

    // Autoteste.
    public bool? TestUpdateAnswer;
    public string? TestUpdateVersion => _update?.Version;
    public void TestStartUpdate() => StartUpdate();
    public bool TestClosed => _disposed;
}
