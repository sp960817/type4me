# Type4Me Windows 11 适配版

这是 Type4Me 的 Windows 11 x64 原生客户端预览版，目标是提供和 macOS 版本接近的语音输入体验：全局快捷键录音、云端语音识别、可选 LLM 文本处理、自动输入到当前窗口、历史记录和基础设置。

## 下载运行

1. 到项目的 GitHub Releases 页面下载 `Type4Me.Win-win-x64.zip`。
2. 解压整个文件夹，不要只单独拷贝 `Type4Me.Win.exe`。
3. 双击 `Type4Me.Win.exe` 启动。
4. 第一次启动会打开设置窗口，填入自己的语音识别和 LLM 服务配置后保存。

如果程序已经在托盘运行，可以右键托盘图标打开设置。也可以用下面的方式强制打开设置窗口：

```powershell
.\Type4Me.Win.exe --show-settings
```

## 重要声明

- 发布包不包含任何 App ID、Access Token、API Key 或个人配置。
- 语音识别和 LLM 都需要使用者自行配置服务商账号和密钥。
- 配置文件、历史记录和日志默认保存在 `%APPDATA%\Type4Me\`。
- 录音音频和文本会发送到你在设置里选择的服务商；请先确认对应服务商的隐私政策。
- 历史记录保存在本机 SQLite 数据库中，卸载或删除 `%APPDATA%\Type4Me\history.db` 前请自行备份。
- 这是 Windows 11 适配预览版，优先保证核心语音输入可用，不包含 macOS 专属能力和本地离线模型。

## 功能

- 托盘常驻和设置窗口。
- 全局快捷键，支持自定义录入、按住说话、按一下开始/再按停止。
- 麦克风录音，使用 NAudio/WASAPI 转为 16kHz 单声道 PCM16。
- 云端 ASR：Volcano/Doubao、Deepgram、Soniox、OpenAI。
- LLM 后处理：Doubao/OpenAI-compatible、Claude。
- 文本输入：写入剪贴板后发送粘贴快捷键，失败时保留复制兜底。
- 历史记录：保存识别文本和处理后文本，支持查看、删除和导出 CSV。
- 文本模式：快速输入、智能润色、英文翻译、Prompt 优化、自定义 Prompt。
- 标点设置：完整添加符号、不添加符号、句末句号不添加。

## 配置位置

```text
%APPDATA%\Type4Me\credentials.json
%APPDATA%\Type4Me\history.db
%APPDATA%\Type4Me\logs\
```

安全字段会用 Windows DPAPI 加密后保存；普通字段保存在 `credentials.json`。请不要把这个目录上传到 GitHub 或发给别人。

## 从源码运行

```powershell
$dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe"
& $dotnet restore .\windows\Type4Me.Windows.slnx
& $dotnet run --project .\windows\Type4Me.Win\Type4Me.Win.csproj
```

## 构建发布包

推荐发布为“文件夹版”，启动更快，也更方便排查依赖问题：

```powershell
$dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe"
& $dotnet publish .\windows\Type4Me.Win\Type4Me.Win.csproj -c Release -r win-x64 --self-contained true -o .\windows\publish\Type4Me.Win-win-x64 /p:PublishSingleFile=false /p:PublishReadyToRun=false
Compress-Archive -Path .\windows\publish\Type4Me.Win-win-x64\* -DestinationPath .\windows\publish\Type4Me.Win-win-x64.zip -Force
```

生成后的下载包在：

```text
windows\publish\Type4Me.Win-win-x64.zip
```

## 测试

```powershell
$dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe"
& $dotnet test .\windows\Type4Me.Windows.slnx
```

## 当前限制

- 仅面向 Windows 11 x64。
- v1 只支持云端语音识别，不内置本地离线 SenseVoice/Qwen3-ASR。
- 不包含 Apple Speech、macOS Accessibility、Mac Action、DMG 更新机制。
- 自动粘贴依赖目标窗口是否允许剪贴板粘贴，少数安全输入框可能会拦截。
