using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;

namespace LazadaHelper;

internal sealed class ShopBrowser : IAsyncDisposable
{
    private ChromeDriver? driver;
    public BrowserPage? Page { get; private set; }
    public bool Ready => driver != null && Page is { IsClosed: false };

    internal static string FindChrome()
    {
        string[] candidates = [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google/Chrome/Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google/Chrome/Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google/Chrome/Application", "chrome.exe")];
        return candidates.FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("未找到正式版 Chrome。请从 https://www.google.com/chrome/ 安装后重试，无需扩展。");
    }

    internal async Task Start(bool headless = false, string? profile = null)
    {
        await DisposeAsync();
        await Task.Run(() =>
        {
            string binary = FindChrome();
            string profilePath = profile ?? Path.Combine(Storage.Root, "chrome-stable-profile");
            Directory.CreateDirectory(profilePath);
            var options = new ChromeOptions { BinaryLocation = binary, PageLoadStrategy = PageLoadStrategy.None, AcceptInsecureCertificates = false };
            options.AddArguments("--user-data-dir=" + profilePath, "--start-maximized");
            if (headless) options.AddArgument("--headless=new");
            using var serviceOnFailure = new ServiceGuard(ChromeDriverService.CreateDefaultService());
            serviceOnFailure.Service.HideCommandPromptWindow = true;
            driver = new ChromeDriver(serviceOnFailure.Service, options, TimeSpan.FromSeconds(30));
            serviceOnFailure.Release();
            driver.Manage().Timeouts().ImplicitWait = TimeSpan.Zero;
            driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(20);
            driver.Manage().Timeouts().AsynchronousJavaScript = TimeSpan.FromSeconds(10);
            driver.ExecuteCdpCommand("Page.addScriptToEvaluateOnNewDocument", new Dictionary<string, object> { ["source"] = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "FastProbe.js")) });
            Page = new BrowserPage(driver);
            if (!headless) driver.Manage().Window.Maximize();
        });
    }
    public async Task Open(string url)
    {
        if (!Rules.IsLazada(url)) throw new InvalidOperationException("请输入 Lazada 新加坡 HTTPS 商品链接或官方短链接。");
        if (!Ready) await Start();
        await Page!.Navigate(url);
        if (!Rules.IsLazada(Page.Url)) throw new InvalidOperationException("跳转到非 Lazada 新加坡域名，已停止。");
    }
    public BrowserElement Buy => Page!.Button("Buy Now");
    public BrowserElement Place => Page!.Button("Place Order Now");
    public Task<bool> Clickable(BrowserElement locator) => locator.Clickable();
    public Task ClickBuy(CancellationToken token) => Buy.ClickAsync(token);
    public Task<string> Title() => Page!.Locator("h1").InnerTextAsync();
    public async ValueTask DisposeAsync()
    {
        var old = driver; driver = null; Page = null;
        if (old != null) await Task.Run(() => { try { old.Quit(); } catch { } finally { old.Dispose(); } });
    }
    private sealed class ServiceGuard(ChromeDriverService service) : IDisposable
    {
        public ChromeDriverService Service { get; } = service;
        private bool released;
        public void Release() => released = true;
        public void Dispose() { if (!released) Service.Dispose(); }
    }
}

internal sealed class BrowserPage(ChromeDriver driver)
{
    internal static readonly string ProbeSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "FastProbe.js"));
    public string Url => driver.Url;
    public bool IsClosed { get { try { return driver.WindowHandles.Count == 0; } catch (WebDriverException) { return true; } } }
    public BrowserElement Locator(string selector) => new(driver, By.CssSelector(selector));
    public BrowserElement Button(string name) => new(driver, By.CssSelector("button, [role='button'], input[type='button'], input[type='submit']"), name);
    public Task Navigate(string url) => Task.Run(async () =>
    {
        string marker = Guid.NewGuid().ToString("N");
        driver.ExecuteScript("window.__lhNavigationMarker=arguments[0];", marker);
        driver.Navigate().GoToUrl(url);
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (deadline.Elapsed.TotalSeconds < 20)
        {
            try { if (Convert.ToBoolean(driver.ExecuteScript("return window.__lhNavigationMarker!==arguments[0] && document.readyState!=='loading';", marker))) return; }
            catch (WebDriverException) { }
            await Task.Delay(50);
        }
        throw new TimeoutException("页面加载超时，请检查浏览器。");
    });
    internal Task<object> Script(string script) => Task.Run(() => driver.ExecuteScript(script));
    internal Task<Dictionary<string, object>> Probe(string name, string sku = "", bool active = false, int interval = 500) => Task.Run(() =>
    {
        return (Dictionary<string, object>)driver.ExecuteScript(ProbeSource + "\nif(arguments[2])window.__lh07.query(arguments[3]);return window.__lh07.read(arguments[0], arguments[1]);", name.ToLowerInvariant(), sku, active, interval);
    });
    internal Task ArmActive(bool enabled) => Script(BrowserPage.ProbeSource + "\nwindow.__lh07.disable();Object.assign(window.__lh07.active,{enabled:" + (enabled ? "true" : "false") + ",stopped:false,positive:false,count:0,nextAt:0,waitSince:0,message:'准备查询'});");
    internal async Task StopActive() { try { await Script("window.__lh07?.disable();"); } catch (WebDriverException) { } }
    internal Task SetContent(string html) => Navigate("data:text/html;charset=utf-8," + Uri.EscapeDataString(html));
}

internal sealed class BrowserElement(ChromeDriver driver, By by, string? name = null)
{
    private List<IWebElement> Matches()
    {
        if (name == null) return driver.FindElements(by).ToList();
        var hit = driver.ExecuteScript(BrowserPage.ProbeSource + "\nreturn window.__lh07.find(arguments[0]).element;", name.ToLowerInvariant());
        return hit is IWebElement element ? [element] : [];
    }
    private IWebElement Unique()
    {
        var elements = Matches();
        if (elements.Count != 1) throw new InvalidOperationException("页面控件无法唯一识别，已暂停。");
        return elements[0];
    }
    public Task<bool> Clickable() => Task.Run(() =>
    {
        try { var elements = Matches(); return elements.Count == 1 && elements[0].Displayed && elements[0].Enabled && elements[0].GetAttribute("aria-disabled") != "true"; }
        catch (StaleElementReferenceException) { return false; }
    });
    public Task<string> InnerTextAsync() => Task.Run(() => Unique().Text);
    public Task ClickAsync(CancellationToken token = default) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        var element = Unique();
        if (!element.Displayed || !element.Enabled || element.GetAttribute("aria-disabled") == "true") throw new InvalidOperationException("按钮尚不可用。");
        token.ThrowIfCancellationRequested();
        try { element.Click(); }
        catch (ElementClickInterceptedException ex) { throw new InvalidOperationException("按钮被弹窗遮挡，请手动处理验证或弹窗后重新启动。", ex); }
        // Do not retry a click: navigation timeouts may occur after the server received it.
    }, token);
}
