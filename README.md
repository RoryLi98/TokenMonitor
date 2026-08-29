# TokenMonitor

TokenMonitor 是一款 Windows 任务栏额度监视器，用来显示本机已登录的 **Codex** 和 **Claude Desktop** 剩余额度及刷新倒计时。界面风格参考 TrafficMonitor，适合长期常驻任务栏。

> [!IMPORTANT]
> **Claude 目前只支持 Claude Desktop 桌面版。** 不支持从 Claude 网页版、Claude API、Anthropic Console 或浏览器登录状态读取额度。

## 功能

- 在 Windows 任务栏中以双行纯文字显示 Codex 和 Claude。
- Codex：显示 5 小时剩余额度、5 小时刷新时间、周剩余额度和周刷新时间。
- Claude Desktop：显示 5 小时剩余额度和估算刷新时间。
- Claude 暂无可读取的周额度数据，任务栏中的周剩余额度和周刷新时间支持填写自定义文字或留空。
- 可分别自定义 Codex/Claude 的：
  - 5h 剩余额度文字；
  - 5h 刷新时间文字；
  - 周剩余额度文字；
  - 周刷新时间文字。
- 可调整字体、字号、项目间距、双行垂直间距和窗口顶部偏移。
- 自动检测任务栏中的 TrafficMonitor 区域并避让，避免两者重叠。
- Explorer 重启后自动重新嵌入和定位。
- 托盘右键可立即刷新、修改自动刷新频率、打开显示设置或隐藏任务栏文本。
- 所有数据均在本机读取，不上传账号凭证、Cookie、OAuth token、API key 或聊天正文。

## 数据来源与支持情况

| 服务 | 数据来源 | 5h 额度 | 5h 刷新时间 | 周额度 | 周刷新时间 |
| --- | --- | :---: | :---: | :---: | :---: |
| Codex | 本机 `codex app-server` | ✅ | ✅ 精确 | ✅ | ✅ 精确 |
| Claude Desktop | 本机 `plan-usage-history.json` | ✅ | ⚠️ 估算 | ❌ 自定义占位文字 | ❌ 自定义占位文字 |
| Claude 网页版 / API | 不支持 | ❌ | ❌ | ❌ | ❌ |

### Codex

TokenMonitor 启动本机 Codex CLI 的 `app-server`，调用 `account/rateLimits/read` 读取当前登录账号的额度窗口。使用前需要安装 Codex，并确保本机已经登录。

### Claude Desktop

TokenMonitor 只读取 **Claude Desktop 桌面应用**保存在本机的用量历史文件。该文件不包含服务端返回的精确刷新时间，因此 5h 刷新时间是根据历史样本估算的，界面会用你设置的文字进行标记。

Claude Desktop 当前没有在该本地文件中提供周额度百分比和周刷新时间，所以这两项不会被 TokenMonitor 猜测；你可以在任务栏显示设置中填写占位文字，或者留空隐藏文字。

## 系统要求

- Windows 10 或 Windows 11（x64）。
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（仅从源码构建时需要）。
- 如需 Codex 数据：本机已安装并登录 Codex。
- 如需 Claude 数据：本机已安装并登录 Claude Desktop。

## 使用方法

1. 启动 `TokenMonitor.exe`。
2. 程序会显示详情窗口，并把双行文本嵌入任务栏。
3. 右键任务栏文本或系统托盘图标，可打开显示设置和刷新选项。
4. 在“任务栏显示设置”中分别编辑 Codex、Claude 的四项显示文字。
5. 关闭详情窗口后，程序仍会在系统托盘运行。

设置保存在：

```text
%LOCALAPPDATA%\TokenMonitor\settings.json
```

设置文件只包含界面与刷新频率配置，不保存账号凭证。

## 从源码构建

```powershell
git clone https://github.com/RoryLi98/TokenMonitor.git
cd TokenMonitor
dotnet build .\TokenMonitor.sln -c Release
```

运行桌面应用：

```powershell
dotnet run --project .\TokenMonitor.App\TokenMonitor.App.csproj
```

运行只读数据探针：

```powershell
dotnet run --project .\TokenMonitor.Probe\TokenMonitor.Probe.csproj
```

发布 Windows x64 自包含单文件：

```powershell
dotnet publish .\TokenMonitor.App\TokenMonitor.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o .\artifacts\win-x64-single
```

## 可选路径覆盖

仅当自动发现失败时使用：

- `TOKENMONITOR_CODEX_PATH`：`codex.exe` 的完整路径。
- `TOKENMONITOR_CLAUDE_USAGE_PATH`：Claude Desktop `plan-usage-history.json` 的完整路径。

## 已知限制

- Claude **仅支持桌面版**，不支持网页版和 API。
- Claude 5h 刷新时间来自本地历史样本，是估算值而非服务端精确时间。
- Claude Desktop 更新可能改变内部历史文件的位置或格式；无法识别时程序会显示读取错误，不会猜测额度。
- Claude Desktop 当前无法提供周额度数据。
- 当前主要针对 Windows 11 底部水平任务栏设计；非标准任务栏工具、垂直任务栏或多显示器组合仍可能需要手动调整位置。
- TokenMonitor 会避让正在运行的 TrafficMonitor，但无法保证识别所有第三方任务栏插件。

## 隐私说明

TokenMonitor 不要求输入账号密码，也不会保存或上传登录凭证。它只在本机读取额度相关数据，并在本机界面中显示结果。

## 免责声明

TokenMonitor 是非官方工具，与 OpenAI、Anthropic 或 TrafficMonitor 项目无隶属关系。Codex 和 Claude Desktop 的内部接口或本地文件格式发生变化时，读取功能可能需要更新。
