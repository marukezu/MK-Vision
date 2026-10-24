namespace MKVision.Desktop;

// Conserta MPEG-TS sem relógio (PCR) — o HLS do YouTube vem assim (a tabela de faixas diz "PCR: nenhum") e o
// leitor do Windows não entrega nenhum quadro sem ele. Aponta a tabela (PMT) para a faixa de vídeo como fonte do
// relógio e, antes de cada quadro de vídeo, põe um pacote só com o PCR (tirado do tempo do quadro). Os pacotes
// originais ficam iguais; arquivos que já têm PCR passam sem mudança. Funciona enquanto o arquivo cresce: lê do
// arquivo baixado e escreve o consertado, até o download terminar.
internal static class TsFixer
{
    private const int Packet = 188;

    // Consertado em "output" enquanto "input" cresce. Termina quando "download" termina e tudo foi copiado.
    public static Task Run(string input, string output, Task download, CancellationToken token) => Task.Run(() =>
    {
        while (!File.Exists(input) && !download.IsCompleted) { token.ThrowIfCancellationRequested(); Thread.Sleep(50); }
        if (!File.Exists(input)) return;
        using var source = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var target = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete);
        var state = new State();
        var buffer = new byte[Packet * 2048];
        int kept = 0; // bytes de um pacote incompleto no começo do buffer
        while (true)
        {
            token.ThrowIfCancellationRequested();
            bool finished = download.IsCompleted;
            int n = source.Read(buffer, kept, buffer.Length - kept);
            if (n <= 0)
            {
                if (finished) break;
                Thread.Sleep(30);
                continue;
            }
            int total = kept + n, at = 0;
            while (total - at >= Packet)
            {
                if (buffer[at] != 0x47) { at++; continue; } // fora de sincronia: procura o próximo pacote
                Process(buffer.AsSpan(at, Packet), target, state);
                at += Packet;
            }
            kept = total - at;
            Buffer.BlockCopy(buffer, at, buffer, 0, kept);
            target.Flush();
        }
    }, token);

    private sealed class State
    {
        public int PmtPid = -1, VideoPid = -1;
        public bool NeedsPcr;                    // a tabela diz que não há PCR
        public readonly Dictionary<int, int> Continuity = [];
    }

    private static void Process(Span<byte> p, FileStream target, State s)
    {
        int pid = ((p[1] & 0x1f) << 8) | p[2];
        bool start = (p[1] & 0x40) != 0;
        int afc = (p[3] >> 4) & 3;
        int payload = 4 + (afc is 2 or 3 ? 1 + p[4] : 0);
        if (afc is 1 or 3) s.Continuity[pid] = p[3] & 0x0f;

        if (pid == 0 && start && payload < Packet - 13)
        {
            // PAT: onde está a PMT.
            int section = payload + 1 + p[payload];
            s.PmtPid = ((p[section + 10] & 0x1f) << 8) | p[section + 11];
        }
        else if (pid == s.PmtPid && start && payload < Packet - 13)
        {
            int section = payload + 1 + p[payload];
            int length = ((p[section + 1] & 0x0f) << 8) | p[section + 2];
            int pcrPid = ((p[section + 8] & 0x1f) << 8) | p[section + 9];
            int infoLength = ((p[section + 10] & 0x0f) << 8) | p[section + 11];
            // Faixa de vídeo (H.264 0x1B, H.265 0x24, MPEG-2 0x02).
            for (int j = section + 12 + infoLength; j + 5 <= section + 3 + length - 4 && j + 5 <= Packet; )
            {
                int type = p[j], esPid = ((p[j + 1] & 0x1f) << 8) | p[j + 2], esInfo = ((p[j + 3] & 0x0f) << 8) | p[j + 4];
                if (type is 0x1B or 0x24 or 0x02 && s.VideoPid < 0) s.VideoPid = esPid;
                j += 5 + esInfo;
            }
            s.NeedsPcr = pcrPid == 0x1fff && s.VideoPid >= 0;
            if (s.NeedsPcr && section + 3 + length <= Packet)
            {
                // A PMT passa a dizer que o relógio vem na faixa de vídeo (e o CRC da seção é refeito).
                p[section + 8] = (byte)((p[section + 8] & 0xe0) | (s.VideoPid >> 8 & 0x1f));
                p[section + 9] = (byte)(s.VideoPid & 0xff);
                int end = section + 3 + length - 4;
                uint crc = Crc32(p[section..end]);
                (p[end], p[end + 1], p[end + 2], p[end + 3]) = ((byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc);
            }
        }
        else if (s.NeedsPcr && pid == s.VideoPid && start && payload + 14 <= Packet &&
                 p[payload] == 0 && p[payload + 1] == 0 && p[payload + 2] == 1 && (p[payload + 7] & 0x80) != 0)
        {
            // Começo de um quadro: PCR = tempo de decodificação (DTS, ou PTS) menos 0,1 s, num pacote só dele.
            int timeAt = (p[payload + 7] & 0x40) != 0 ? payload + 14 : payload + 9; // DTS vem depois do PTS
            if (timeAt + 5 > Packet) timeAt = payload + 9;
            long time = ((long)(p[timeAt] >> 1) & 7) << 30 | (long)p[timeAt + 1] << 22 | (long)(p[timeAt + 2] >> 1) << 15 | (long)p[timeAt + 3] << 7 | (long)p[timeAt + 4] >> 1;
            long pcr = Math.Max(0, time - 9000);
            Span<byte> extra = stackalloc byte[Packet];
            extra.Fill(0xff);
            extra[0] = 0x47;
            extra[1] = (byte)(pid >> 8 & 0x1f);
            extra[2] = (byte)(pid & 0xff);
            s.Continuity.TryGetValue(pid, out int cc);
            extra[3] = (byte)(0x20 | ((cc - 1) & 0x0f)); // só adaptação: o contador não avança (repete o do último com dados)
            extra[4] = 183;                                // tamanho da adaptação (resto do pacote)
            extra[5] = 0x10;                               // tem PCR
            extra[6] = (byte)(pcr >> 25);
            extra[7] = (byte)(pcr >> 17);
            extra[8] = (byte)(pcr >> 9);
            extra[9] = (byte)(pcr >> 1);
            extra[10] = (byte)(((pcr & 1) << 7) | 0x7e);
            extra[11] = 0;
            target.Write(extra);
        }
        target.Write(p);
    }

    // CRC-32 do MPEG-2 (polinômio 0x04C11DB7, sem reflexão).
    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xffffffff;
        foreach (byte b in data)
        {
            crc ^= (uint)b << 24;
            for (int k = 0; k < 8; k++) crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
        }
        return crc;
    }
}
