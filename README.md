# MAODIE EdgePanel (耄耋侧边栏)

WinUI 3 桌面侧边栏工具,集成 AI 助手、剪贴板、音乐、天气、待办、OCR、启动器等多种功能。

## 下载

前往 [Releases](../../releases) 页面下载 `EdgePanel-release-win-x64.zip`,解压后运行 `EdgePanel.exe`。

> 需要 .NET 10 Desktop Runtime 与 Windows App SDK 运行时;系统需 Windows 10 1809+。

## 构建

```bash
dotnet publish EdgePanel.csproj -c Release -r win-x64 -p:Platform=x64 -p:SelfContained=false -p:WindowsAppSDKSelfContained=false
```

## 协议

[MIT](LICENSE)
