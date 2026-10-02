namespace LazadaHelper;

internal sealed class StockChime : IDisposable
{
    private readonly System.Media.SoundPlayer player = new(Path.Combine(AppContext.BaseDirectory, "stock-dingdong.wav"));
    public StockChime() { try { player.Load(); } catch { } }
    // SoundPlayer.Play is asynchronous; audio failures must not interrupt purchasing.
    public void Play() { try { player.Play(); } catch { try { System.Media.SystemSounds.Asterisk.Play(); } catch { } } }
    public void Dispose() => player.Dispose();
}
