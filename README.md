# Lazada Capture / Lazada 助手

Windows 桌面工具，使用电脑上安装的正式版 Chrome 监测 Lazada.sg 商品，并提供库存声音提醒、购买按钮操作和测试到结算页功能。

**当前版本 v0.9.0 为测试版：主动 API 查询尚未通过真实网站验证。** 本地模拟测试通过不代表真实补货检测或下单成功；网站要求验证时需要人工处理。

## 下载和运行

从 [Releases](https://github.com/alenzhu72/lazada-capture/releases) 下载 `LazadaHelper-v0.9-Windows-x64.zip`，完整解压后运行 `LazadaHelper.exe`。需要 Windows x64 和已安装的 Google Chrome；程序包包含 .NET 运行时，无需浏览器扩展。首次启动驱动管理组件可能需要联网。

输入 Lazada.sg 商品链接，在程序打开的 Chrome 中登录。先使用“测试到结算页”检查流程；测试模式不提交订单。正式自动提交需单独勾选。总额上限为可选设置，默认关闭。

详细操作及限制见 [使用说明](使用说明-v0.9.md)。浏览器登录资料保存于本机 `%LOCALAPPDATA%\LazadaHelper`，不包含在本仓库和发布包中。

## 功能

- 任意 Lazada.sg 商品链接，不固定商品或规格。
- 页面按钮监测、库存变化叮咚提示、浏览器最大化和秒钟显示。
- 主动查询实验功能：调用页面提供的 MTop 组件，默认在响应结束后等待 500 毫秒；同一时刻只允许一个请求。
- 有货后操作 Buy Now，测试模式停在提交前；正式模式按设置操作 Place Order。
- 验证、会话异常或组件不可用时暂停；不绕过网站验证。

## 从源码构建

安装 .NET 10 SDK，在仓库目录执行：

```powershell
dotnet publish LazadaHelper.csproj -c Release -r win-x64 --self-contained true -o publish
```

技术栈：.NET 10 WinForms、Selenium.WebDriver 4.29.0。请保留发布目录里的脚本、音效和运行库。

## 验证

主动查询模拟测试需要 Node.js：

```powershell
node tests/active-query.cjs
```

构建后可执行内置规则测试及本地浏览器模拟测试（后者需要 Chrome）：

```powershell
New-Item -ItemType Directory -Force test-results
Start-Process ./publish/LazadaHelper.exe -ArgumentList '--self-test test-results/rules.txt' -Wait
Start-Process ./publish/LazadaHelper.exe -ArgumentList '--browser-self-test test-results/browser.txt' -Wait
```

历史验证记录见 [validation-v0.9.txt](tests/validation-v0.9.txt)：14 项规则、23 项浏览器模拟、10 项主动查询模拟。真实只读检查停在 Lazada 验证页，没有成功执行主动查询，也未实际购买。
