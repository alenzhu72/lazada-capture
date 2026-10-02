using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;


namespace LazadaHelper;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length == 2 && args[0] == "--self-test")
        {
            try
            {
                Storage.TestRoot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, "test-data");
                string report = SelfTests.Run();
                using (var form = new MainForm())
                {
                    form.ShowInTaskbar = false; form.Opacity = 0; form.Show();
                    form.PerformLayout(); Application.DoEvents();
                    using var bitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                    bitmap.Save(Path.ChangeExtension(args[1], ".png"));
                }
                File.WriteAllText(args[1], report); Environment.ExitCode = 0;
            }
            catch (Exception ex) { File.WriteAllText(args[1], ex.ToString()); Environment.ExitCode = 1; }
            return;
        }
        if (args.Length == 2 && args[0] == "--api-live-check")
        {
            Storage.TestRoot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, "test-data");
            try { File.WriteAllText(args[1], SelfTests.LiveApiCheck().GetAwaiter().GetResult()); }
            catch { File.WriteAllText(args[1], "Live check failed; no credentials or raw responses logged."); Environment.ExitCode=1; }
            return;
        }
        if (args.Length == 2 && args[0] == "--browser-self-test")
        {
            Storage.TestRoot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, "test-data");
            try { File.WriteAllText(args[1], SelfTests.BrowserRun().GetAwaiter().GetResult()); }
            catch (Exception ex) { File.WriteAllText(args[1], ex.ToString()); Environment.ExitCode = 1; }
            return;
        }
        using var mutex = new Mutex(true, @"Local\LazadaHelper-SingleInstance", out bool first);
        if (!first) { MessageBox.Show("应用已经运行，请使用已有窗口。", "Lazada 助手"); return; }
        Application.Run(new MainForm());
    }
}

internal sealed record Settings
{
    public string TargetUrl { get; set; } = "https://s.lazada.sg/s.TkVZb?c=w";
    public string TestUrl { get; set; } = "";
    public int ScanMs { get; set; } = 100;
    public decimal MaxTotal { get; set; } = 0;
    public bool LimitTotal { get; set; } = false;
    public bool ActiveApi { get; set; } = true;
    public int ApiIntervalMs { get; set; } = 500;
}

internal static class Storage
{
    internal static string? TestRoot;
    public static string Root => TestRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LazadaHelper");
    public static Settings Load()
    {
        Directory.CreateDirectory(Root);
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(Root, "settings.json"))) ?? new(); }
        catch { return new(); }
    }
    public static void Save(Settings settings) => File.WriteAllText(Path.Combine(Root, "settings.json"), JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
}

internal static class Rules
{
    public static bool IsLazada(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "https" && u.IsDefaultPort && string.IsNullOrEmpty(u.UserInfo) && (u.Host.Equals("lazada.sg", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".lazada.sg", StringComparison.OrdinalIgnoreCase));
    public static string Normalize(string value) => Regex.Replace(value.Normalize(), @"\s+", " ").Trim();
    public static string ProductKey(string url) => Regex.Match(url, @"-i(\d+)-s(\d+)\.html", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value + ":" + m.Groups[2].Value : "";
    public static decimal CheckCheckout(string text, decimal limit)
    {
        if (limit <= 0) throw new InvalidOperationException("请设置包含运费的最高订单总额。 ");
        // Anchor to a complete line, so Subtotal/discounted or unrelated prices cannot be treated as Total.
        var totals = Regex.Matches(text.Replace("\r", ""), @"(?im)^\s*Total\s*:?\s*(?:SGD\s*|S\s*)?\$\s*([0-9]+(?:,[0-9]{3})*(?:\.[0-9]{2})?)\s*$");
        if (totals.Count != 1 || !decimal.TryParse(totals[0].Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal total) || total <= 0) throw new InvalidOperationException("无法唯一识别订单 Total；停止自动提交。 ");
        if (total > limit) throw new InvalidOperationException($"订单总额 S${total:F2} 超过上限 S${limit:F2}。 ");
        return total;
    }
}

internal sealed class MainForm : Form
{
    private readonly Settings settings = Storage.Load();
    private readonly ShopBrowser browser = new();
    private readonly StockChime stockChime = new();
    private readonly TextBox target = new(), test = new(), max = new();
    private readonly CheckBox limitTotal = new() { Text = "启用订单总额上限（SGD，含运费）", AutoSize = true };
    private readonly NumericUpDown scan = new() { Minimum = 50, Maximum = 5000, Increment = 50 };
    private readonly CheckBox activeApi = new() { Text = "主动查询API", AutoSize = true };
    private readonly NumericUpDown apiInterval = new() { Minimum = 100, Maximum = 10000, Increment = 100, Width = 80 };
    private readonly CheckBox auto = new() { Text = "正式监测：允许自动提交订单（数量按网页选择）", AutoSize = true };
    private readonly Label wallClock = new() { AutoSize = true, ForeColor = Color.FromArgb(0, 97, 136), TextAlign = ContentAlignment.MiddleRight };
    private readonly System.Windows.Forms.Timer clockTimer = new() { Interval = 200 };
    private readonly Label status = new() { Text = "待机 · 先打开商品，登录并选好规格", AutoSize = true, ForeColor = Color.FromArgb(0, 97, 136) };
    private readonly TextBox log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.FromArgb(245, 247, 251), BorderStyle = BorderStyle.FixedSingle };
    private readonly Button openTarget = new() { Text = "打开目标商品" }, openTest = new() { Text = "打开测试商品" }, runTarget = new() { Text = "开始目标监测" }, runTest = new() { Text = "测试到结算页" }, stop = new() { Text = "停止", Enabled = false };
    private readonly List<Control> locked = [];
    private CancellationTokenSource? cancellation;
    private bool busy, closing;
    private string openedMode = "", openedUrl = "";

    public MainForm()
    {
        Text = "Lazada 监测与购买助手 · v0.9 · 主动API查询";
        Font = new Font("Microsoft YaHei UI", 10);
        ClientSize = new Size(920, 700); MinimumSize = new Size(840, 650); StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.White;
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 2, RowCount = 12 };
        grid.ColumnStyles.Add(new(SizeType.Absolute, 150)); grid.ColumnStyles.Add(new(SizeType.Percent, 100));
        grid.RowStyles.Add(new(SizeType.Absolute, 64));
        for (int i = 1; i <= 5; i++) grid.RowStyles.Add(new(SizeType.Absolute, 42));
        grid.RowStyles.Add(new(SizeType.Absolute, 42)); grid.RowStyles.Add(new(SizeType.Absolute, 46)); grid.RowStyles.Add(new(SizeType.Absolute, 44)); grid.RowStyles.Add(new(SizeType.Absolute, 44)); grid.RowStyles.Add(new(SizeType.Absolute, 30)); grid.RowStyles.Add(new(SizeType.Percent, 100));
        Controls.Add(grid);
        var heading = new Label { Text = "Lazada 购买助手", Font = new Font(Font.FontFamily, 21, FontStyle.Bold), AutoSize = true };
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        header.ColumnStyles.Add(new(SizeType.Percent, 50)); header.ColumnStyles.Add(new(SizeType.Percent, 50));
        header.Controls.Add(heading, 0, 0); wallClock.Dock = DockStyle.Fill; header.Controls.Add(wallClock, 1, 0);
        grid.Controls.Add(header, 0, 0); grid.SetColumnSpan(header, 2);
        UpdateClock(); clockTimer.Tick += (_, _) => UpdateClock(); clockTimer.Start();
        Disposed += (_, _) => { clockTimer.Dispose(); stockChime.Dispose(); };
        AddRow(grid, 1, "目标商品链接", target); AddRow(grid, 2, "测试商品链接", test);
        var timing = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false }; scan.Width = 90;
        timing.Controls.AddRange([scan, new Label { Text = "毫秒检测", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, activeApi, apiInterval, new Label { Text = "毫秒/查询（返回后计时）", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }]);
        AddRow(grid, 3, "监测间隔", timing); AddRow(grid, 4, "金额限制（可选）", limitTotal); AddRow(grid, 5, "最高总额 SGD", max);
        max.PlaceholderText = "仅勾选上方选项时填写";
        limitTotal.CheckedChanged += (_, _) => max.Enabled = limitTotal.Checked && !busy;
        grid.Controls.Add(auto, 1, 6);
        var buttons1 = new FlowLayoutPanel { Dock = DockStyle.Fill }; buttons1.Controls.AddRange([openTarget, runTarget, stop]);
        var buttons2 = new FlowLayoutPanel { Dock = DockStyle.Fill }; buttons2.Controls.AddRange([openTest, runTest]);
        foreach (var button in new[] { openTarget, runTarget, stop, openTest, runTest }) { button.AutoSize = true; button.Height = 34; button.Padding = new Padding(8, 0, 8, 0); }
        AddRow(grid, 7, "正式商品", buttons1); AddRow(grid, 8, "测试商品", buttons2);
        var hint = new Label { Text = "主动查询发现有货后响铃并刷新一次，接着检测购买按钮；查询失败会暂停。\n每次执行一轮，测试停在下单前。金额上限可选，商品数量以网页为准。", Dock = DockStyle.Fill, ForeColor = Color.DimGray, Font = new Font(Font.FontFamily, 9) };
        grid.Controls.Add(hint, 0, 9); grid.SetColumnSpan(hint, 2); grid.Controls.Add(status, 0, 10); grid.SetColumnSpan(status, 2); grid.Controls.Add(log, 0, 11); grid.SetColumnSpan(log, 2);
        target.Text = settings.TargetUrl; test.Text = settings.TestUrl; limitTotal.Checked = settings.LimitTotal;
        activeApi.Checked = settings.ActiveApi; apiInterval.Value = Math.Clamp(settings.ApiIntervalMs, 100, 10000);
        scan.Value = Math.Clamp(settings.ScanMs, 50, 5000); max.Text = settings.LimitTotal && settings.MaxTotal > 0 ? settings.MaxTotal.ToString("0.00", CultureInfo.InvariantCulture) : ""; max.Enabled = limitTotal.Checked;
        locked.AddRange([target, test, limitTotal, scan, activeApi, apiInterval, max, auto, openTarget, openTest, runTarget, runTest]);
        openTarget.Click += async (_, _) => await Open(false); openTest.Click += async (_, _) => await Open(true);
        runTarget.Click += async (_, _) => await Run(false); runTest.Click += async (_, _) => await Run(true);
        stop.Click += (_, _) => { cancellation?.Cancel(); Log("正在停止；已发出的点击不能撤销，程序不会再提交新的点击。 "); };
        FormClosing += async (_, e) =>
        {
            if (closing) return; e.Cancel = true; cancellation?.Cancel();
            if (busy) { Log("任务正在退出，请稍后再次关闭窗口。 "); return; }
            closing = true; Save(); await browser.DisposeAsync(); Close();
        };
        Log("支持任意 Lazada 新加坡站链接。请打开页面并选择商品；未启用金额上限时无需填写。 ");
    }
    private void UpdateClock() => wallClock.Text = $"本机时间  {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
    private static void AddRow(TableLayoutPanel grid, int row, string label, Control control)
    {
        grid.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, Padding = new Padding(0, 5, 0, 0) }, 0, row);
        control.Dock = DockStyle.Fill; grid.Controls.Add(control, 1, row);
    }
    private void Save()
    {
        settings.TargetUrl = target.Text.Trim(); settings.TestUrl = test.Text.Trim(); settings.ScanMs = (int)scan.Value; settings.LimitTotal = limitTotal.Checked;
        settings.MaxTotal = decimal.TryParse(max.Text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0; Storage.Save(settings);
        settings.ActiveApi = activeApi.Checked; settings.ApiIntervalMs = (int)apiInterval.Value; Storage.Save(settings);
    }
    private void SetBusy(bool value)
    {
        busy = value; foreach (var c in locked) c.Enabled = !value; stop.Enabled = value && cancellation != null; max.Enabled = !value && limitTotal.Checked;
    }
    private void Log(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff}  {message}";
        status.Text = message.Trim(); log.AppendText(line + Environment.NewLine);
        try { File.AppendAllText(Path.Combine(Storage.Root, "activity.log"), line + Environment.NewLine); } catch { }
    }
    private async Task Open(bool testing)
    {
        if (busy) return;
        try
        {
            Save(); SetBusy(true); Log("正在打开本机正式版 Chrome（独立登录资料）…");
            openedMode = ""; string url = testing ? settings.TestUrl : settings.TargetUrl;
            await browser.Open(url); openedMode = testing ? "test" : "target"; openedUrl = url;
            Log("页面已打开。请手动登录、选好规格和所需数量，再点击开始。 ");
        }
        catch (Exception ex) { Log("打开失败：" + ex.Message); }
        finally { SetBusy(false); }
    }
    private async Task Run(bool testing)
    {
        if (busy) return;
        bool submit = !testing && auto.Checked;
        try
        {
            Save();
            if (!browser.Ready || openedMode != (testing ? "test" : "target") || openedUrl != (testing ? settings.TestUrl : settings.TargetUrl)) throw new InvalidOperationException("请先用对应的“打开商品”按钮打开当前链接。 ");
            if (submit && settings.LimitTotal && settings.MaxTotal <= 0) throw new InvalidOperationException("已勾选总额上限，请填写大于0的金额（含运费）。 ");
            cancellation = new(); SetBusy(true); var token = cancellation.Token; var page = browser.Page!;
            string initialUrl = page.Url;
            if (!Rules.IsLazada(initialUrl)) throw new InvalidOperationException("当前页面不是 Lazada 新加坡站。");
            Log($"{(testing ? "测试" : submit ? "自动下单" : "演练")}开始；不限定商品或规格；页面检测 {settings.ScanMs}ms。");
            string budgetText = settings.LimitTotal ? $"S${settings.MaxTotal:F2}（含运费）" : "未启用，不限制订单总额";
            if (submit && MessageBox.Show($"商品、规格和数量：以网页当前选择为准\n金额上限：{budgetText}\n\n检测到可购买按钮后自动点击，并提交当前结算页订单一次。是否启动？", "启动自动购买", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            var diagnostic = Stopwatch.StartNew(); var totalTime = Stopwatch.StartNew();
            bool reportedWaiting = false, stockAlerted = false;
            bool refreshedForStock = false;
            int stockRefreshes = 0;
            double refreshedAt = 0;
            string lastApiMessage = "";
            await page.ArmActive(settings.ActiveApi);

            while (true)
            {
                token.ThrowIfCancellationRequested();
                var scanWatch = Stopwatch.StartNew();
                var probe = await page.Probe("Buy Now", active: settings.ActiveApi && !refreshedForStock, interval: settings.ApiIntervalMs);
                double scanElapsed = scanWatch.Elapsed.TotalMilliseconds;
                string currentUrl = Convert.ToString(probe["url"]) ?? "";
                if (!Rules.IsLazada(currentUrl)) throw new InvalidOperationException("商品页面发生变化；任务已停止。");
                if (settings.ActiveApi && !refreshedForStock)
                {
                    string apiMessage = Convert.ToString(probe["activeMessage"]) ?? "";
                    if (apiMessage != lastApiMessage) { Log($"API：{apiMessage}；已查询{probe["activeCount"]}次，上次耗时{probe["activeDuration"]}ms"); lastApiMessage = apiMessage; }
                    if (Convert.ToBoolean(probe["activeStopped"])) throw new InvalidOperationException(apiMessage);
                    if (Convert.ToBoolean(probe["activePositive"]) && probe["element"] == null)
                    {
                        if (!stockAlerted) { stockAlerted = true; stockChime.Play(); }
                        refreshedForStock = true;
                        stockRefreshes++;
                        await page.StopActive(); token.ThrowIfCancellationRequested();
                        Log("主动查询发现有货，刷新商品页一次后立即检测购买按钮…");
                        await page.Navigate(currentUrl);
                        refreshedAt = totalTime.Elapsed.TotalSeconds;
                        token.ThrowIfCancellationRequested();
                        continue;
                    }
                }
                if (!stockAlerted && Convert.ToBoolean(probe["apiAvailable"]))
                {
                    stockAlerted = true; stockChime.Play();
                    Log($"API提示当前SKU可购买（上限{probe["apiMax"]}），等待页面购买按钮就绪。");
                }
                if (probe["element"] != null)
                {
                    token.ThrowIfCancellationRequested(); if (!stockAlerted) { stockAlerted = true; stockChime.Play(); } Log($"检测到可购买，扫描耗时{scanElapsed:F1}ms；立即点击 Buy Now…");
                    // One click attempt; never retry after an ambiguous click/navigation timeout.
                    await page.StopActive();
                    await browser.ClickBuy(token);
                    token.ThrowIfCancellationRequested();
                    await Checkout(testing || !submit, token);
                    Log($"本次任务结束；总耗时 {totalTime.Elapsed.TotalSeconds:F1} 秒。 ");
                    break;
                }
                if (refreshedForStock && totalTime.Elapsed.TotalSeconds - refreshedAt > 5)
                {
                    if (stockRefreshes >= 3) throw new InvalidOperationException("接口提示有货，但连续3次刷新后购买按钮仍不可用，请检查规格、库存或页面验证。");
                    Log("刷新后仍不可购买，恢复主动查询。");
                    await page.ArmActive(true); refreshedForStock = false;
                }
                if (!reportedWaiting) { Log("等待库存：检测 Buy Now；缺货或按钮未就绪。 "); reportedWaiting = true; }
                if (diagnostic.ElapsedMilliseconds >= 5000)
                {
                    Log($"监测中：{probe["reason"]}；扫描{scanElapsed:F1}ms；匹配{probe["count"]}/可见{probe["visible"]}/可用{probe["enabled"]}；API响应{probe["apiCount"]}次，{probe["apiError"]}");
                    var text = await page.Locator("body").InnerTextAsync();
                    if (Regex.IsMatch(text, @"verify you are human|slide to verify|unusual traffic|access denied|too many requests", RegexOptions.IgnoreCase)) throw new InvalidOperationException("页面要求验证或限制访问，请手动处理后再启动。 ");
                    diagnostic.Restart();
                }
                await Task.Delay(Math.Max(1, settings.ScanMs - (int)scanWatch.ElapsedMilliseconds), token);
            }
        }
        catch (OperationCanceledException) { Log("任务已停止。已发出的操作请在浏览器中核对。 "); }
        catch (Exception ex) { Log("任务暂停：" + ex.Message); }
        finally { if (browser.Page != null) await browser.Page.StopActive(); cancellation?.Dispose(); cancellation = null; SetBusy(false); }
    }
    private async Task Checkout(bool rehearsal, CancellationToken token)
    {
        var page = browser.Page!; var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < 45)
        {
            token.ThrowIfCancellationRequested();
            var checkoutProbe = await page.Probe("Place Order Now");
            if (!Rules.IsLazada(Convert.ToString(checkoutProbe["url"]) ?? "")) throw new InvalidOperationException("跳转到非 Lazada 页面，请手动检查。");
            if (checkoutProbe["element"] != null)
            {
                Log($"结算按钮就绪，用时 {clock.Elapsed.TotalSeconds:F2} 秒。 ");
                if (rehearsal) { Log("测试通过：已到结算页，未点击 Place Order Now。请检查商品、规格及费用。 "); return; }
                if (settings.LimitTotal)
                {
                    string text = await page.Locator("body").InnerTextAsync();
                    decimal amount = Rules.CheckCheckout(text, settings.MaxTotal);
                    Log($"总额 S${amount:F2} 未超上限，准备提交当前订单…");
                }
                else Log("未启用金额上限，准备提交当前网页订单…");
                token.ThrowIfCancellationRequested();
                await browser.Place.ClickAsync(token);
                Log("已发出下单点击。请在浏览器确认订单或完成付款验证；不会自动重试。 ");
                System.Media.SystemSounds.Exclamation.Play();
                return;
            }
            await Task.Delay(50, token);
        }
        throw new InvalidOperationException("45秒内未出现可用的 Place Order Now。请检查登录、库存、地址或验证提示。 ");
    }
}

internal static class SelfTests
{
    internal static async Task<string> LiveApiCheck()
    {
        await using var shop = new ShopBrowser();
        await shop.Start(true, Path.Combine(Storage.Root,"live-api-check-profile"));
        await shop.Open("https://www.lazada.sg/products/pdp-i13913354068-s124920101406.html");
        await Task.Delay(8000);
        var page=shop.Page!;
        var flags=await page.Script("return JSON.stringify({title:document.title,text:document.body?.innerText?.slice(0,250),scripts:[...document.scripts].map(s=>s.src.split('?')[0]).filter(Boolean).slice(0,25),sdk:typeof(window.lib?.mtop?.request),alternative:typeof(window.mtop?.request),template:!!window.__lh07?.template,sku:window.__lh07?.template?.sku,apiCount:window.__lh07?.apiCount});");
        await page.ArmActive(true);
        await page.Probe("Buy Now",active:true);
        await Task.Delay(7000);
        var p=await page.Probe("Buy Now"); await page.StopActive();
        return flags+"\nActive: "+p["activeMessage"]+"\nQueries: "+p["activeCount"]+"\nPositive: "+p["activePositive"];
    }
    public static async Task<string> BrowserRun()
    {
        await using var shop = new ShopBrowser();
        await shop.Start(true, Path.Combine(Storage.Root, "self-test-chrome-profile"));
        var page = shop.Page!;
        var lines = new List<string>();
        async Task Check(string name, Func<Task<bool>> test)
        { if (!await test()) throw new Exception(name); lines.Add("PASS " + name); }
        await page.SetContent("<h1>Fixture Deck Case</h1><button>Add to Wishlist</button>");
        await Check("Out-of-stock rejected", async () => !await shop.Clickable(shop.Buy));
        await page.Script("document.querySelector('button').textContent='Buy Now'; document.querySelector('button').disabled=true;");
        await Check("Disabled Buy rejected", async () => !await shop.Clickable(shop.Buy));
        await page.Script("document.querySelector('button').disabled=false;");
        await Check("Available Buy detected", () => shop.Clickable(shop.Buy));
        await Check("Product title", async () => await shop.Title() == "Fixture Deck Case");
        await page.Script("document.body.appendChild(document.querySelector('button').cloneNode(true));");
        await Check("Ambiguous Buy rejected", async () => !await shop.Clickable(shop.Buy));
        foreach (var quantity in new[] { "<input value='1'>", "<input value='3'>", "<span>Qty: 2</span>" })
        {
            await page.SetContent(quantity + "<button id='buy'>Buy Now</button>");
            await page.Script("document.querySelector('button').onclick=()=>{document.body.innerHTML='<button>PLACE ORDER NOW</button>';};");
            await shop.ClickBuy(CancellationToken.None);
            await Check("Buy to checkout with " + quantity, () => shop.Clickable(shop.Place));
        }
        await page.Script("window.orders=0; document.querySelector('button').onclick=()=>{window.orders++;};");
        await Check("Rehearsal has not submitted", async () => Convert.ToInt64(await page.Script("return window.orders;")) == 0);
        await shop.Place.ClickAsync();
        await Check("Order clicked once", async () => Convert.ToInt64(await page.Script("return window.orders;")) == 1);
        await page.Script("document.querySelector('button').disabled=true;");
        await Check("Disabled Place rejected", async () => !await shop.Clickable(shop.Place));
        await page.SetContent("<button>Buy Now</button>");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await shop.ClickBuy(cancelled.Token); throw new Exception("Cancelled click allowed"); }
        catch (OperationCanceledException) { lines.Add("PASS Cancellation"); }
        await page.Script("document.querySelector('button').setAttribute('aria-disabled','true');");
        await Check("ARIA disabled rejected", async () => !await shop.Clickable(shop.Buy));
        await page.SetContent("<h1>Fixture Deck Case</h1><button style='display:none'>Buy Now</button><button aria-label=''>Buy Now</button>");
        await Check("Hidden duplicate and empty aria-label do not hide available button", async () => (await page.Probe("Buy Now"))["element"] != null);
        await page.SetContent("<button><span>Buy   Now</span></button>");
        await Check("Nested text and whitespace", async () => (await page.Probe("Buy Now"))["element"] != null);
        await page.SetContent("<button>Add to Wishlist</button>");
        await page.Probe("Buy Now");
        await page.Script("window.__lh07.accept('https://acs-m.lazada.sg/h5/mtop.global.detail.web.getdetailinfo/1.0/', {ret:['SUCCESS::ok'],data:{module:JSON.stringify({skuInfos:{'123':{skuId:'123',quantity:{limit:{max:2},text:'Available'}}}})}});");
        await Check("API positive signal with no buy button", async () => { var p = await page.Probe("Buy Now", "123"); return Convert.ToBoolean(p["apiAvailable"]) && p["element"] == null; });
        await Check("API other SKU ignored", async () => !Convert.ToBoolean((await page.Probe("Buy Now", "456"))["apiAvailable"]));
        await page.Script("window.__lh07.api['123'].at=Date.now()-16000;");
        await Check("Stale API signal ignored", async () => !Convert.ToBoolean((await page.Probe("Buy Now", "123"))["apiAvailable"]));
        await page.Script("window.__lh07.accept('https://acs-m.lazada.sg/h5/mtop.global.detail.web.getdetailinfo/1.0/', {ret:['SUCCESS::ok'],data:{module:{skuInfos:{'123':{skuId:'123',quantity:{limit:{max:0},text:'Out of stock'}}}}}});");
        await Check("API zero does not trigger", async () => !Convert.ToBoolean((await page.Probe("Buy Now", "123"))["apiAvailable"]));
        await page.Script("window.__lh07.accept('https://acs-m.lazada.sg/h5/mtop.global.detail.web.getdetailinfo/1.0/', 'invalid-json');");
        await Check("Malformed API response diagnosed", async () => !string.IsNullOrEmpty(Convert.ToString((await page.Probe("Buy Now", "123"))["apiError"])));
        await page.Script("setTimeout(()=>{document.querySelector('button').textContent='Buy Now'; setTimeout(()=>document.querySelector('button').textContent='Add to Wishlist',250);},150);");
        bool transientSeen = false; var polling = Stopwatch.StartNew();
        while (polling.ElapsedMilliseconds < 1500) { if ((await page.Probe("Buy Now"))["element"] != null) { transientSeen = true; break; } await Task.Delay(50); }
        await Check("250ms transient stock detected", () => Task.FromResult(transientSeen));
        await page.SetContent("<button>Buy Now</button><button>Buy Now</button>");
        await Check("Two visible buttons still rejected", async () => (await page.Probe("Buy Now"))["element"] == null);
        using (var fixture = new ApiFixture())
        {
            await page.Navigate(fixture.Url);
            var apiWait = Stopwatch.StartNew(); bool hooked = false;
            while (apiWait.Elapsed.TotalSeconds < 5)
            {
                var p = await page.Probe("Buy Now", "123");
                if (Convert.ToInt64(p["apiCount"]) == 2 && Convert.ToBoolean(p["apiAvailable"])) { hooked = true; break; }
                await Task.Delay(50);
            }
            await Check("Fetch and XHR passive capture installed before page scripts", () => Task.FromResult(hooked));
        }
        return string.Join(Environment.NewLine, lines) + "\nInstalled Chrome; local synthetic pages only. No live purchases.\n";
    }
    public static string Run()
    {
        var results = new List<string>();
        void Test(string name, Action action) { action(); results.Add("PASS " + name); }
        void Assert(bool ok) { if (!ok) throw new Exception("Assertion failed"); }
        void Reject(Action action) { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Expected rejection"); }
        const string valid = "Shipping Address\nDeck Case\nQty: 1\nSubtotal (1 Items)\n$6.30\nShipping Fee\n$1.99\nTotal:\n$8.29\nPLACE ORDER NOW";
        Test("Lazada links allowed", () => Assert(Rules.IsLazada("https://s.lazada.sg/s.TkVZb?c=w") && Rules.IsLazada("https://www.lazada.sg/products/a")));
        Test("Lookalike domains and non-HTTPS rejected", () => Assert(!Rules.IsLazada("https://lazada.sg.evil.example/") && !Rules.IsLazada("http://www.lazada.sg/") && !Rules.IsLazada("https://user@lazada.sg/") && !Rules.IsLazada("https://lazada.sg:444/")));
        Test("Product and SKU key", () => Assert(Rules.ProductKey("https://www.lazada.sg/products/x-i13858018841-s124858765958.html?a=1") == "13858018841:124858765958"));
        Test("Checkout total including shipping", () => Assert(Rules.CheckCheckout(valid, 9) == 8.29m));
        Test("Over budget rejected", () => Reject(() => Rules.CheckCheckout(valid, 8)));
        Test("Missing budget rejected", () => Reject(() => Rules.CheckCheckout(valid, 0)));
        Test("No product restriction", () => Assert(Rules.CheckCheckout(valid.Replace("Deck Case", "Another item"), 9) == 8.29m));
        Test("Budget defaults off", () => Assert(!new Settings().LimitTotal));
        Test("Multiple quantity allowed", () => Assert(Rules.CheckCheckout(valid.Replace("Qty: 1", "Qty: 2"), 20) == 8.29m));
        Test("Multiple items allowed", () => Assert(Rules.CheckCheckout(valid.Replace("1 Items", "2 Items") + "\nSecond product\nQty: 3", 20) == 8.29m));
        Test("Duplicate total rejected", () => Reject(() => Rules.CheckCheckout(valid + "\nTotal: $7.00", 20)));
        Test("Subtotal alone is not total", () => Reject(() => Rules.CheckCheckout(valid.Replace("Total:\n$8.29", ""), 20)));
        Test("Quantity label not required", () => Assert(Rules.CheckCheckout(valid.Replace("Qty: 1", ""), 20) == 8.29m));
        Test("SGD formatted total", () => Assert(Rules.CheckCheckout(valid.Replace("$8.29", "SGD $8.29"), 9) == 8.29m));
        return string.Join(Environment.NewLine, results) + $"\n{results.Count} tests passed. No live purchases performed.\n";
    }
}
