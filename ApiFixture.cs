using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LazadaHelper;

// Local-only test fixture. Never sends requests to Lazada.
internal sealed class ApiFixture : IDisposable
{
    private readonly HttpListener listener = new();
    public string Url { get; }
    public ApiFixture()
    {
        var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start();
        int port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        Url = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(Url); listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try
                {
                    var ctx = await listener.GetContextAsync();
                    bool api = ctx.Request.Url!.AbsolutePath.Contains("mtop.global.detail.web.getdetailinfo");
                    string body = api ? """
                    {"ret":["SUCCESS::ok"],"data":{"module":"{\"skuInfos\":{\"123\":{\"skuId\":\"123\",\"quantity\":{\"limit\":{\"max\":2}}}}}"}}
                    """ : """
                    <html><body><h1>Fixture</h1><button>Add to Wishlist</button><script>
                    fetch('/h5/mtop.global.detail.web.getdetailinfo/1.0/?fetch').then(r=>r.json());
                    const x=new XMLHttpRequest();x.open('GET','/h5/mtop.global.detail.web.getdetailinfo/1.0/?xhr');x.send();
                    </script></body></html>
                    """;
                    var bytes = Encoding.UTF8.GetBytes(body);
                    ctx.Response.ContentType = api ? "application/json" : "text/html";
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes); ctx.Response.Close();
                }
                catch (Exception) when (!listener.IsListening) { break; }
            }
        });
    }
    public void Dispose() { listener.Stop(); listener.Close(); }
}
