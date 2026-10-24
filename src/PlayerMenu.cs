using System.Diagnostics;

namespace MKVision.Desktop;

// Botão direito na imagem do player: o que hoje só tem atalho de teclado (para usar no óculos, sem teclado). Voltar ao
// navegador no topo; o resto em seções por assunto (com o nome em cinza): arquivo, edição e IA, efeitos, imagem (girar,
// espelhar) e vídeo. Cada item mostra o atalho ao lado; o que não vale agora (salvar vídeo numa foto, por exemplo) nem
// aparece; o que liga e desliga aparece marcado quando ligado. Nas barras e na lista, nada.
internal sealed partial class Player
{
    // Text vazio = separador; Sub = submenu; Radio = marca redonda (uma opção entre várias); Header = nome da seção (cinza,
    // não clica).
    private sealed record MenuEntry(string Text, Action? Run = null, bool Checked = false, MenuEntry[]? Sub = null, bool Radio = false, bool Header = false);

    private static readonly MenuEntry MenuSeparator = new("");
    private static readonly double[] MenuSpeeds = [0.25, 0.5, 0.75, 1, 1.5, 2];
    private static readonly int[] MenuStrengths = [0, 50, 75, 100, 125, 150, 200, 300, 400];
    private const uint MFT_RADIOCHECK = 0x200, MF_DISABLED = 0x2;

    private List<MenuEntry> PlayerMenuEntries()
    {
        bool video, photo, annotating, cropping, comfyShown = false, showComfy, aiShown = false, showAi, showTone, loop, fullscreen, flipH, flipV;
        bool bypass, sharpenOff, rtx, smooth, crisp, ensemble, trained;
        double speed, zoom;
        int files, rotation, power;
        float strength;
        PlayerProfile? profile;
        string? local;
        lock (_gpuLock)
        lock (_lock)
        {
            (video, photo, annotating, cropping, speed, files, showComfy) =
                (_file is not null && !_image, _image, _annotating, _cropping, _speed, _folderFiles.Length, _showComfy);
            (showAi, showTone, loop, fullscreen, flipH, flipV, rotation, zoom) = (_showAi, _showTone, _loop, _fullscreen, _flipH, _flipV, _rotation, _zoom);
            (bypass, sharpenOff, rtx, power, strength) = (_settings.Bypass, _settings.SharpenOff, _settings.RtxVideo, _settings.Power, _settings.Strength);
            (smooth, crisp, ensemble, trained, profile) = (_prefs.Smooth, _prefs.Crisp, _prefs.Ensemble, _prefs.TrainedModel, _prefs.CurrentProfile);
            local = _folderIndex >= 0 && _folderIndex < _folderFiles.Length ? _folderFiles[_folderIndex] : null;
            if (ScreenFrame() is { } frame && _stillSerial == frame.Serial)
            {
                comfyShown = _comfyLayer is not null;
                aiShown = (_showComfy ? _aiComfy : _aiOriginal) is not null;
            }
        }
        if (local is not null && (IsWeb(local) || IsWebKey(local) || !File.Exists(local))) local = null;
        bool comfyRunning = _comfyJob is { IsCancellationRequested: false };
        Action Key(int key) => () => OnKey(key); // o mesmo que a tecla (avisos e tudo)
        static MenuEntry Section(string name) => new(name, Header: true);
        var back = new MenuEntry("Voltar para o navegador\tF2", () => ShowWeb(true));
        // Versão nova no GitHub (PlayerUpdate): logo abaixo do navegador.
        MenuEntry[] update = UpdateMenuText() is { } u ? [new(u, StartUpdate)] : [];
        if (!video && !photo) return [back, .. update, MenuSeparator, new("Abrir...\tCtrl+O", ShowOpenDialog)];
        // Cortando (F4): só o que vale nele.
        if (cropping) return [new("Aceitar o corte\tEnter", AcceptCrop), new("Cancelar o corte\tEsc", CancelCrop), MenuSeparator,
            new("Imagem inteira", () => CropAction(CropActFull))];

        var entries = new List<MenuEntry> { back };
        entries.AddRange(update);
        entries.AddRange([MenuSeparator, Section("Arquivo")]);
        entries.Add(new(video ? "Salvar o quadro como imagem\tCtrl+S" : "Salvar imagem\tCtrl+S", SavePhotoAi));
        if (video) entries.Add(new("Salvar vídeo\tShift+S", SaveVideo));
        if (local is not null) entries.Add(new("Mostrar no Explorer", () => ShowInExplorer(local)));
        if (files > 1)
        {
            entries.Add(new("Anterior da pasta\tPage Up", () => OpenSibling(-1)));
            entries.Add(new("Próximo da pasta\tPage Down", () => OpenSibling(1)));
        }

        entries.AddRange([MenuSeparator, Section("Edição e IA")]);
        entries.Add(comfyRunning ? new("Cancelar o ComfyUI\tShift+G", () => ComfyKey(cancel: true)) : new("Editar com o ComfyUI...\tG", () => ComfyKey(cancel: false)));
        entries.Add(new("Desenhar / marcar área\tF3", ToggleAnnotate, annotating));
        entries.Add(new("Cortar\tF4", ToggleCrop));
        entries.Add(new("Melhorar com a IA de fotos\tI", EnhanceShown));
        if (PackMenuEntry() is { } pack) entries.Add(pack);

        entries.AddRange([MenuSeparator, Section("Efeitos")]);
        if (comfyShown) entries.Add(new(showComfy ? "Ver a original\tT" : "Ver a resposta do ComfyUI\tT", Key('T')));
        if (aiShown) entries.Add(new("Melhoria da IA de fotos\tU", Key('U'), showAi));
        entries.Add(new("Luz e cor\tY", Key('Y'), showTone));
        entries.Add(new("Comparar sem ajustes\tB", Key('B'), bypass));
        entries.Add(new("Nitidez do player\tD", Key('D'), !sharpenOff));
        entries.Add(new("RTX Video\tR", Key('R'), rtx));
        entries.Add(new($"Perfil ({profile?.Name ?? "personalizado"})", Sub: [.. PlayerProfile.All.Select(p => new MenuEntry(p.Name, () =>
        {
            _prefs.Apply(p);
            Notify($"Perfil: {p.Name}");
        }, p == profile, Radio: true)), MenuSeparator, new("Próximo perfil\tP", Key('P'))]));
        entries.Add(new($"Força ({strength * 100:0}%)", Sub: [.. MenuStrengths.Select(v => new MenuEntry($"{v}%", () =>
        {
            _settings.Strength = v / 100f;
            Notify($"Força {v}%");
        }, Math.Abs(strength * 100 - v) < 0.5, Radio: true)), MenuSeparator, new("Mais força\t+", Key(PlayerNative.VK_ADD)), new("Menos força\t−", Key(PlayerNative.VK_SUBTRACT))]));
        entries.Add(new($"Potência da IA ({power})", Sub: [.. Enumerable.Range(1, 5).Select(i =>
            new MenuEntry($"{i} · {PowerNames[i]}\t{i}", Key('0' + i), power == i, Radio: true))]));

        entries.AddRange([MenuSeparator, Section("Imagem")]);
        entries.Add(new("Girar 90° para a esquerda", () => Rotate(-90)));
        entries.Add(new("Girar 90° para a direita", () => Rotate(90)));
        entries.Add(new("Girar 45° para a esquerda\tInsert", () => Rotate(-45)));
        entries.Add(new("Girar 45° para a direita\tDelete", () => Rotate(45)));
        if (rotation != 0) entries.Add(new("Sem giro", () => Rotate(-rotation)));
        entries.Add(new("Espelhar na horizontal\tHome", () => Flip(horizontal: true), flipH));
        entries.Add(new("Espelhar na vertical\tEnd", () => Flip(horizontal: false), flipV));
        if (zoom > 1.0001) entries.Add(new("Tirar o zoom\tZ", Key('Z')));
        entries.Add(new("Tela cheia\tF", Key('F'), fullscreen));

        if (video)
        {
            entries.AddRange([MenuSeparator, Section("Vídeo")]);
            entries.Add(new("Velocidade", Sub: [.. MenuSpeeds.Select(s =>
                new MenuEntry(s == 1 ? "1x (normal)\tBackspace" : $"{s.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("pt-BR"))}x",
                    () => SetSpeed(s), Math.Abs(speed - s) < 0.001, Radio: true))]));
            entries.Add(new("Quadro anterior\tShift+←", () => StepFrame(-1)));
            entries.Add(new("Próximo quadro\tShift+→", () => StepFrame(1)));
            entries.Add(new("Repetir\tL", Key('L'), loop));
            var slow = new List<MenuEntry>
            {
                new("Criar quadros com IA abaixo de 1x\tS", Key('S'), smooth),
                new("Sem fantasma onde algo cobre\tN", Key('N'), crisp),
                new("Análise dupla (menos deformação)\tE", Key('E'), ensemble)
            };
            if (RifeHost.HasTrained) slow.Add(new("IA treinada com seus vídeos\tM", Key('M'), trained));
            entries.Add(new("Câmera lenta", Sub: [.. slow]));
        }
        return entries;
    }

    // WM_RBUTTONUP fora do navegador: o menu, se foi na imagem (ou na tela vazia).
    private void PlayerRightClick(int x, int y)
    {
        lock (_lock)
        {
            (_mouseX, _mouseY, _mouseInside) = (x, y, true);
            if (!_cropping && HitTest(x, y) is not (UiPart.Video or UiPart.None)) return;
        }
        var entries = PlayerMenuEntries();
        if (TestMenuChoice is { } choice)
        {
            // Autoteste: sem a janela do menu (ela prende a thread); escolhe pelo texto.
            TestMenuShown = MenuLabels(entries, "");
            TestMenuChoice = null;
            if (FindMenu(entries, "", choice) is { } chosen) RunMenu(chosen);
            return;
        }
        var actions = new List<Action>();
        int id = Menu(menu => FillMenu(menu, entries, actions));
        if (id > 0) RunMenu(actions[id - 1]);
    }

    private static void FillMenu(IntPtr menu, IEnumerable<MenuEntry> entries, List<Action> actions)
    {
        foreach (var entry in entries)
        {
            if (entry.Text == "") AppendMenu(menu, MF_SEPARATOR, 0, null);
            else if (entry.Header) AppendMenu(menu, MF_STRING | MF_GRAYED | MF_DISABLED, 0, entry.Text);
            else if (entry.Sub is { } sub)
            {
                var child = CreatePopupMenu();
                FillMenu(child, sub, actions);
                AppendMenu(menu, MF_POPUP, child, entry.Text);
            }
            else
            {
                actions.Add(entry.Run!);
                AppendMenu(menu, MF_STRING | (entry.Checked ? MF_CHECKED : 0) | (entry.Radio ? MFT_RADIOCHECK : 0), actions.Count, entry.Text);
            }
        }
    }

    private void RunMenu(Action action)
    {
        action();
        _panel?.Refresh();
        OnSettingsChanged(); // como depois de uma tecla: título, tela e preferências
    }

    private void ShowInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception error)
        {
            Notify($"Não consegui abrir o Explorer: {error.Message}");
        }
    }

    // Os textos do menu (sem o atalho), com "Submenu/item" e " ✓" no marcado.
    private static string[] MenuLabels(IEnumerable<MenuEntry> entries, string prefix) =>
        [.. entries.Where(e => e.Text != "" && !e.Header).SelectMany(e => e.Sub is { } sub
            ? MenuLabels(sub, prefix + e.Text.Split('\t')[0] + "/")
            : [prefix + e.Text.Split('\t')[0] + (e.Checked ? " ✓" : "")])];

    private static Action? FindMenu(IEnumerable<MenuEntry> entries, string prefix, string label)
    {
        foreach (var entry in entries.Where(e => e.Text != "" && !e.Header))
        {
            string text = prefix + entry.Text.Split('\t')[0];
            if (entry.Sub is { } sub && FindMenu(sub, text + "/", label) is { } found) return found;
            if (entry.Sub is null && text == label) return entry.Run;
        }
        return null;
    }

    // Autoteste.
    public string? TestMenuChoice;
    public string[] TestMenuShown = [];
    public string[] TestMenu() => MenuLabels(PlayerMenuEntries(), "");
}
