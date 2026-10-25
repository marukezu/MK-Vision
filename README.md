# MK Vision Desktop

Player de vídeo e foto para Windows com melhoria de imagem por IA, junto com um navegador embutido (o motor do Microsoft Edge) que manda os vídeos e as imagens dos sites direto para o player.

O app foi pensado para assistir com a melhor imagem possível, inclusive em óculos de realidade virtual (Meta Quest pelo Virtual Desktop ou similares). Por isso, tudo o que tem atalho de teclado também está no **botão direito do mouse**.

---

## Sumário

- [Download e instalação](#download-e-instalação)
- [O player](#o-player)
- [O navegador](#o-navegador)
- [ComfyUI (opcional)](#comfyui-opcional)
- [As IAs da NVIDIA](#as-ias-da-nvidia)
- [Atalhos](#atalhos)
- [Onde ficam os dados](#onde-ficam-os-dados)
- [Compilar a partir do código](#compilar-a-partir-do-código)
- [Créditos e licenças](#créditos-e-licenças)

---

## Download e instalação

1. Baixe o `.zip` da versão mais recente em **[Releases](https://github.com/marukezu/MK-Vision/releases)**.
2. Descompacte em qualquer pasta e abra o **`MKVisionDesktop.exe`**.

Não precisa instalar nada: o .NET vem junto e o navegador usa o WebView2, que já faz parte do Windows 10 e 11.

> Como o programa não é assinado, o Windows pode mostrar **"O Windows protegeu o computador"**. Clique em **Mais informações → Executar assim mesmo**.

**Requisitos**

| | Mínimo | Para as IAs |
|---|---|---|
| Sistema | Windows 10 ou 11, 64 bits | — |
| Placa de vídeo | Qualquer uma com DirectX 11 | NVIDIA (de preferência **RTX**), driver atualizado |
| Espaço | ~750 MB | + ~4,5 GB para as bibliotecas da NVIDIA |

**Atualizações:** ao abrir, o app verifica se há uma versão nova no GitHub. Havendo, ele avisa na tela e aparece **"Atualizar para a versão X"** no botão direito. O app baixa a versão nova, fecha e abre de novo já atualizado. Configurações, favoritos e o resto continuam.

---

## O player

Abra um vídeo ou uma foto com **Ctrl+O**, arrastando para a janela, clicando na tela vazia (abre a lista de pastas) ou pelo navegador. O MK Vision também pode ser o **player padrão de vídeos** do Windows (botão direito → Tornar o MK Vision o player padrão).

### Abas

Cada imagem ou vídeo abre na **sua própria aba**, e cada aba guarda a sua sessão: a posição do vídeo, a velocidade, o zoom, o giro e o espelho, o corte, as anotações, as versões da IA e a resposta do ComfyUI. Trocar de aba volta tudo como estava.

- A faixa das abas aparece com o mouse no **topo da janela** (também na tela cheia).
- Abrir algo que **já está aberto** vai para a aba dele; senão, abre uma aba nova ao lado da atual.
- **Page Up / Page Down**, o carrossel do Instagram e a pasta trocam o conteúdo **dentro da própria aba**.
- Clique troca de aba; o **X** ou o **botão do meio** fecha; **Tab** / **Shift+Tab** passam para a aba seguinte / anterior; **Ctrl+W** fecha a da tela.
- Só a aba da tela usa a placa de vídeo: o vídeo de uma aba de fundo fica pausado e volta na mesma posição.

### Imagem melhor em tempo real

- **Nitidez e detalhe:** micro-detalhe, textura da pele, redução de ruído e de blocos de compressão, com **perfis** prontos (Padrão, Pele / Detalhe, Vídeo ruim, Suave) e **força** de 0 a 400%.
- **Ampliação com IA:** quando a tela (ou o zoom) é maior que o vídeo, uma IA própria amplia a imagem, com **potência de 1 a 5**.
- **RTX Video Super Resolution:** em placas NVIDIA RTX, o app usa a ampliação da própria NVIDIA (ligue "Super Resolution" no NVIDIA App).
- **Cor e luz:** brilho, contraste, clareza, vibração e saturação. Há uma dica pronta para o Quest 2, cuja tela apaga um pouco as cores.
- **Comparar** sem os ajustes (tecla B) a qualquer momento.

### Câmera lenta com IA

Abaixo de 1x, a IA (RIFE) **cria os quadros que faltam** entre os quadros reais. A câmera lenta fica suave em vez de "travada". Há opções para menos deformação e menos "fantasma" em objetos que se cruzam.

### Fotos e quadros

- **IA de fotos (I):** amplia 4x com cinco modelos: Natural, Nítida, Máxima, Rosto e Rosto nítido. O realce de textura e o grão evitam o efeito "plástico".
- **Luz e cor (Y):** corrige o equilíbrio de branco e a exposição com IA.
- **Corte (F4):** moldura com proporções (Livre, Original, 1:1, 4:3, 16:9), **Aceitar** e **Cancelar**, como no Fotos do Windows.
- **Anotações (F3):** pincel e retângulo em seis cores para desenhar sobre a imagem.
- **Girar e espelhar** em passos de 45° e 90°, **zoom** com a roda do mouse e arrastar para mover.
- **Salvar:** Ctrl+S salva a imagem da tela, com IA, corte e anotações; Shift+S salva o vídeo. O "Salvar como" abre na última pasta usada.

### Pasta e lista

**Page Up / Page Down** passam pelos arquivos da pasta (e pelos itens de um post do Instagram). A **lista de pastas** abre pela borda direita da janela, com miniaturas.

---

## O navegador

**F2** alterna entre o player e o navegador. Ele usa o motor do Edge e foi feito para parecer o Edge: as abas são o topo da janela.

### Mandar para o player

- **Botão "Abrir no player"** sobre os vídeos: aparece ao passar o mouse sobre um vídeo, em qualquer site, inclusive no YouTube.
- **Tecla "."** abre a imagem sob o mouse ou o vídeo da tela.
- **Botão direito → Abrir no player** em imagens e vídeos.
- **Modo automático:** abre no player os vídeos que começam a tocar.
- **Ctrl+S** baixa o vídeo da página para a pasta Downloads\MK Vision.

O endereço real do vídeo é descoberto pela página ou pelo **yt-dlp**, que o app baixa e mantém atualizado sozinho. Funciona com YouTube e muitos outros sites. No **Instagram** funcionam posts, carrosséis (Page Up/Down pelos itens), reels e stories; galerias de imagens também passam com Page Up/Down.

### Como no Edge

- **Abas:** o botão do meio abre atrás; Ctrl+Tab e Ctrl+1…9; arrastar para reordenar; abas paradas "dormem" para poupar memória; aviso de som.
- **Favoritos:** barra e pastas, importados do Edge na primeira vez. A barra de endereço sugere a partir dos favoritos.
- **Barra de endereço:** o clique seleciona o endereço inteiro; o botão direito tem **"Colar e ir"**.
- **Botão direito na página:** "Abrir link em uma nova aba", "Pesquisar no Google por…" e "Traduzir para o português".
- **Zoom por site** (lembrado), **F11** em tela cheia, **downloads** (Ctrl+J).
- **Bloqueador de anúncios** (uBlock Origin Lite), que pode ser desligado por site, e **modo celular** por site.
- **Permissões por site** (câmera, microfone, localização…) e **apagar os dados** de um site.
- **Tema** claro/escuro para os sites e opção de **escurecer** os sites claros.

---

## ComfyUI (opcional)

A tecla **G** manda a imagem da tela (com giro, corte e anotações) para workflows do [ComfyUI](https://github.com/comfyanonymous/ComfyUI) instalados no computador. Cada workflow vira uma aba da janela do G, com prompt, negativo, LoRAs, passos, CFG, resolução e seed. A resposta aparece no player como uma nova versão da imagem: **T** alterna com a original e **Ctrl+S** salva.

- **Área marcada:** contorne uma área com o F3 (retângulo ou um círculo à mão) e o modelo edita só ali. Fora dela, a imagem original volta intacta.
- **Duas imagens:** a aba de 2 imagens tem um botão para escolher a segunda (`<image2>` no prompt).
- **Fila por aba:** cada aba do player pode mandar o seu pedido; eles rodam um por vez, na ordem, e a resposta volta para a aba que pediu, mesmo que você esteja em outra (a aba mostra o andamento: *gerando 40%*, *fila 2º*, *pronto ✓*). Fechar a aba cancela o pedido dela.
- **Andamento:** *Passo 03/25 · 00:42 decorridos · 03:27 restantes (8,3 s por passo)*. O tempo conta desde que o ComfyUI começa o pedido (o carregamento do modelo entra; a espera na fila, não).
- **Vídeo:** os workflows de vídeo (Wan) abrem o resultado numa aba nova, ao lado da imagem de origem.

O app procura o ComfyUI em `C:\ComfyUI` (ou na pasta da variável `MKV_COMFY_DIR`) e liga o servidor sozinho quando preciso. Os workflows e os modelos ficam no próprio ComfyUI.

---

## As IAs da NVIDIA

A IA de fotos, a câmera lenta com IA e a luz e cor na placa usam o **TensorRT** da NVIDIA. Essas bibliotecas (CUDA 12.8, cuDNN 9.27 e TensorRT 10.9) têm cerca de 4,5 GB, por isso **não vêm no .zip**:

- **Numa placa NVIDIA:** na primeira vez que você usar uma IA, o app pergunta se pode baixá-las (cerca de 3 GB, uma vez só) **direto dos pacotes oficiais da NVIDIA**. Também dá para baixar pelo botão direito → **Baixar as IAs da NVIDIA**.
- **Em outras placas:** o player e o navegador funcionam normalmente; só essas IAs ficam desligadas.
- **Primeiro uso de cada IA:** o TensorRT prepara a IA para a sua placa, o que leva alguns minutos uma vez só. Depois, ela abre em segundos.

---

## Atalhos

### Player

| Tecla | Ação |
|---|---|
| Espaço | Tocar / pausar |
| ← / → | −5 s / +5 s |
| Shift+← / Shift+→ | Quadro anterior / próximo |
| ↑ / ↓ / Backspace | Velocidade +0,1 / −0,1 / normal |
| Page Up / Page Down | Arquivo anterior / próximo da pasta (na mesma aba) |
| Tab / Shift+Tab | Aba seguinte (→) / anterior (←) |
| Ctrl+W | Fechar a aba |
| F ou Enter | Tela cheia |
| Esc | Sai da tela cheia (ou volta ao site de onde o vídeo veio) |
| Z | Tira o zoom |
| Insert / Delete | Girar 45° |
| Home / End | Espelhar na horizontal / vertical |
| F2 | Navegador |
| F3 | Anotações |
| F4 | Cortar |
| G / Shift+G | ComfyUI / cancelar |
| I / U | IA de fotos / liga e desliga a melhoria |
| T | Original ↔ resposta do ComfyUI |
| Y | Luz e cor |
| Ctrl+S / Shift+S | Salvar a imagem / o vídeo |
| Ctrl+O | Abrir arquivo |
| A | Painel de ajustes |
| P | Próximo perfil de nitidez |
| + / − | Força |
| 1 a 5 | Potência da IA de ampliação |
| B | Comparar sem os ajustes |
| D | Nitidez do player |
| R | RTX Video |
| L | Repetir |
| S / N / E / M | Câmera lenta: quadros com IA / sem fantasma / análise dupla / IA treinada |

### Navegador

| Tecla | Ação |
|---|---|
| F2 | Player |
| . | Abrir no player (imagem sob o mouse ou o vídeo da tela) |
| Ctrl+T / Ctrl+W | Nova aba / fechar aba |
| Ctrl+Tab / Ctrl+1…9 | Trocar de aba |
| Ctrl+L | Barra de endereço |
| Alt+Enter | Abrir o endereço em nova aba |
| Ctrl+D | Favoritar |
| Ctrl+S | Baixar o vídeo da página |
| Ctrl+J | Downloads |
| Ctrl + / Ctrl − / Ctrl+0 | Zoom |
| F5 / F11 | Recarregar / tela cheia |
| Alt+Home | Página inicial |

---

## Onde ficam os dados

Tudo fica em `%LOCALAPPDATA%\MKVisionDesktop`, longe da pasta do app (atualizar ou apagar a pasta do app não perde nada):

| Pasta / arquivo | O que é |
|---|---|
| `player.json` | Ajustes do player |
| `bookmarks.json` | Favoritos |
| `browser\` | Perfil do navegador (logins, cookies) |
| `nvidia\` | As bibliotecas da NVIDIA |
| `trt-cache\` | As IAs já preparadas para a sua placa |
| `tools\` | yt-dlp e Deno (baixados e atualizados sozinhos) |
| `log.txt` | Registro do que o app fez (útil para relatar problemas) |

Para remover tudo: apague a pasta do app e essa pasta.

---

## Compilar a partir do código

Requer o [.NET 10 SDK](https://dotnet.microsoft.com/download) no Windows.

```bat
dotnet build src -c Release
```

- **`Iniciar MK Vision.bat`:** compila, se houver mudanças, e abre o app.
- **`Gerar pasta portatil.bat`:** gera `dist\MK Vision Desktop` e o `.zip`, como o das Releases.

O app tem autotestes embutidos, rodados pela linha de comando com o nome do teste, por exemplo:

```bat
src\bin\Debug\net10.0-windows10.0.22621.0\MKVisionDesktop.exe --croptest C:\pasta\de\teste
```

Os testes ficam em `src\*Test.cs`. Os do navegador usam `MKV_SETTINGS`, `MKV_LOG` e `MKV_WEB_PROFILE` para não mexer nos dados de verdade.

**Publicar uma versão:** ao enviar uma tag `v*` (ex.: `v1.0.1-beta`), o GitHub Actions compila, monta o `.zip` e publica a Release. Os apps já instalados se atualizam a partir dela.

---

## Créditos e licenças

Modelos de IA incluídos (detalhes em `src\Models\**\CREDITOS.txt`):

| Modelo | Uso | Autor | Licença |
|---|---|---|---|
| FSRCNNX | Ampliação em tempo real | igv / HelpSeeker | LGPL 3.0 |
| RIFE v4.26 | Câmera lenta | hzwer (conversão: vs-mlrt) | MIT |
| Real-ESRGAN x4plus | IA de fotos (Natural) | Xintao Wang et al. | BSD-3-Clause |
| 4x-UltraSharp | IA de fotos (Nítida) | Kim2091 | CC BY-NC-SA 4.0 (não comercial) |
| 4xRealWebPhoto v4 DAT2 | IA de fotos (Máxima) | Philip Hofmann | CC BY 4.0 |
| 4xFaceUpDAT / 4xFaceUpSharpDAT | IA de fotos (Rosto) | Philip Hofmann | CC BY 4.0 |
| Deep White-Balance | Luz e cor | Mahmoud Afifi, Michael S. Brown | CC BY-NC-SA 4.0 (não comercial) |
| Illumination Adaptive Transformer | Luz e cor | Ziteng Cui et al. | Apache 2.0 |

Componentes de terceiros: [ONNX Runtime](https://github.com/microsoft/onnxruntime) (MIT), [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) (MIT), [WebView2](https://developer.microsoft.com/microsoft-edge/webview2/), [yt-dlp](https://github.com/yt-dlp/yt-dlp) (Unlicense), [Deno](https://github.com/denoland/deno) (MIT), [uBlock Origin Lite](https://github.com/uBlockOrigin/uBOL-home) (GPL 3.0) e as bibliotecas da NVIDIA (CUDA, cuDNN, TensorRT), baixadas dos pacotes oficiais sob as licenças da NVIDIA.

Como alguns modelos são de uso **não comercial**, o MK Vision é distribuído gratuitamente.
