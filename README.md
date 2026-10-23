# MAODIE EdgePanel（耄耋侧边栏）

一款基于 **WinUI 3** 打造的桌面侧边栏工具，停靠在屏幕边缘，随用随呼出，集成了十余种日常高频功能。

## ✨ 功能特性

- **AI 助手** — 内置 AI 对话，随时提问
- **启动器** — 快速搜索并启动本机应用
- **剪贴板历史** — 自动记录剪贴板内容，随时回溯粘贴
- **音乐播放** — 本地音乐播放，带桌面歌词
- **天气** — 实时天气与预报展示
- **待办事项** — 轻量待办清单
- **便签笔记** — 快速记录想法
- **OCR 文字识别** — 内置 RapidOCR，截图取字
- **网络工具** — 网络状态监控
- **系统工具** — 系统信息一览
- **密码保险库** — 本地加密存储敏感信息
- **定时器 / 跑马灯 / 弹窗监控** 等更多小工具

## 📦 下载安装

前往 [Releases](../../releases) 页面下载 `EdgePanel-release-win-x64.zip`，解压后运行 `EdgePanel.exe`，无需安装。

### 运行要求

| 项目 | 要求 |
|---|---|
| 操作系统 | Windows 10 1809 (17763) 及以上，64 位 |
| 运行时 | .NET 10 Desktop Runtime + Windows App SDK 运行时 |
| 架构 | x64 |

> 本版本为**非自包含**发布，体积小；若启动提示缺少运行时，请先安装 [.NET Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)。

## 🛠️ 源码构建

```bash
git clone https://github.com/wyq138/MAODIE-EdgePanel.git
cd MAODIE-EdgePanel
dotnet publish EdgePanel.csproj -c Release -r win-x64 -p:Platform=x64 -p:SelfContained=false -p:WindowsAppSDKSelfContained=false
```

产物输出在 `publish_release/` 目录。

### 技术栈

- WinUI 3 / Windows App SDK 2.5
- .NET 10（C# latest）
- RapidOcrNet（离线 OCR）
- Dubya.WindowsMediaController（媒体控制）

## 📁 项目结构

```
├── EdgePanel.csproj      # 主工程
├── Pages/                # 各功能页面（AI、剪贴板、音乐、天气…）
├── Services/             # 服务层（剪贴板监听、系统监控、更新…）
├── Core/                 # 设置与原生交互
├── Controls/             # 自定义控件
├── ToastBridge/          # 通知子进程（Native AOT）
└── Assets/               # 图标与 OCR 模型资源
```

## 📄 协议

本项目基于 [MIT License](LICENSE) 开源，欢迎自由使用、修改与分发。
