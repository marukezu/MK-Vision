using System.Net.WebSockets;
using System.Text;

namespace MKVision.Desktop;

// "--linktest": abre a ligação e simula a extensão (e um site tentando se passar por ela).
internal static class LinkTest
{
    public static int Run()
    {
        var settings = new Settings();
        using var link = new Link(settings);
        var url = new Uri($"ws://127.0.0.1:{Link.Port}/");

        // Um site comum (origem http) deve ser recusado.
        using (var site = new ClientWebSocket())
        {
            site.Options.SetRequestHeader("Origin", "https://site-qualquer.com");
            try { site.ConnectAsync(url, CancellationToken.None).Wait(3000); Console.WriteLine($"Site: conectou (ERRADO) {site.State}"); }
            catch { Console.WriteLine("Site: recusado (certo)"); }
        }

        using var extension = new ClientWebSocket();
        extension.Options.SetRequestHeader("Origin", "chrome-extension://abcdefghijklmnop");
        extension.ConnectAsync(url, CancellationToken.None).Wait(3000);
        Console.WriteLine($"Extensão: {extension.State}");

        void Send(string json) => extension.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None).Wait();
        Send("""{"type":"hello","browser":"edge"}""");
        Send("""{"type":"settings","detail":1.6,"detailRadius":0.7,"texture":0.5,"textureRadius":3,"threshold":2,"power":5}""");
        Send("""{"type":"geometry","dpr":1.25,"innerWidth":2048,"innerHeight":1036,"fullscreen":false,"video":{"x":100,"y":80,"w":1280,"h":720,"vw":854,"vh":480,"ux":0.25,"uy":0.25,"uw":0.5,"uh":0.5}}""");
        Thread.Sleep(300);

        var g = link.GeometryFor("edge");
        Console.WriteLine($"Potência recebida: {settings.Power}");
        Console.WriteLine(g is null ? "Geometria: NÃO recebida" : $"Geometria: vídeo {g.VideoWidth}x{g.VideoHeight} em ({g.X},{g.Y}) {g.Width}x{g.Height} CSS, dpr {g.Dpr}, lupa uv ({g.UvX},{g.UvY}) {g.UvWidth}");

        // O app avisa a extensão que está processando.
        link.SetState("edge", true);
        var buffer = new byte[256];
        var received = extension.ReceiveAsync(buffer, CancellationToken.None);
        Console.WriteLine(received.Wait(2000) ? $"Extensão recebeu: {Encoding.UTF8.GetString(buffer, 0, received.Result.Count)}" : "Extensão não recebeu o estado");
        return g is not null && settings.Power == 5 ? 0 : 1;
    }
}
