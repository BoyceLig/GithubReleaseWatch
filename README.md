# GithubReleaseWatch

一个用于监测多个 GitHub 仓库 Release 版本更新的 Windows 桌面工具。

## 功能

- **多仓库监测** —— 以卡片列表管理任意多个 GitHub 仓库，支持拖拽排序
- **版本比较** —— 自动对比"当前版本"与"最新版本"，徽章提示 `有更新` / `最新` / `失败`
- **版本说明** —— 选中仓库即展开最近 3 个版本及其完整 Release Notes（可拖选复制）
- **Pre-release 开关** —— 一键切换是否显示预发布版本
- **代理支持** —— HTTP / SOCKS5 代理，支持可选账号密码
- **Token 支持** —— 配置 GitHub Token 可将 API 配额从 60 次/小时提升到 5000 次/小时
- **配额显示** —— 主面板与设置面板实时显示已用/剩余 API 配额（`/rate_limit` 端点不计配额）
- **失效地址自动迁移** —— 仓库改名时 GitHub 返回 301，程序自动跟随并更新本地 URL
- **添加查重** —— 重复添加同一仓库会弹窗拦截

<img width="1136" height="693" alt="image" src="https://github.com/user-attachments/assets/b7a8d32e-e248-4e39-90d1-d28bc2872bf9" />
<img width="506" height="253" alt="image" src="https://github.com/user-attachments/assets/297d9b43-a633-475c-9dfd-0024bc52faaa" />
<img width="506" height="533" alt="image" src="https://github.com/user-attachments/assets/f63ede44-5490-432e-946e-d9a29cdaddc4" />
<img width="486" height="491" alt="image" src="https://github.com/user-attachments/assets/335b070c-6a03-4c5f-9d14-5426d65a2fd0" />


## 环境要求

- Windows 10 1809 及以上 / Windows 11
- Microsoft Edge WebView2 Runtime（版本说明使用组合式 WebView2 显示）
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（含 ".NET 桌面开发" 工作负载）
- Visual Studio 2022（可选，推荐）

## 运行方式

下载并解压运行Releases最新版的exe

## 配置文件

程序首次运行会在 **exe 同目录**生成 `config.json`，结构如下：

```json
{
  "Repos": [
    {
      "Url": "https://github.com/BoyceLig/GithubReleaseWatch",
      "Owner": "BoyceLig",
      "Repo": "GithubReleaseWatch",
      "CurrentVersion": "v1.0.1"
    }
  ],
  "ShowPrerelease": false,
  "Token": null,
  "TokenEnabled": false,
  "Proxy": {
    "Enabled": true,
    "Type": "Socks5",
    "Host": "127.0.0.1",
    "Port": 7897,
    "Username": null,
    "Password": null
  },
  "RefreshOnStartup": true
}
```

> 也可以手动编辑 `config.json` 批量导入仓库，重启程序即可生效。

## 关于 API 配额

GitHub 未认证请求的配额为 **60 次/小时**，且按**出口 IP** 统计 —— 使用公共代理节点时可能被他人共享消耗。

配置并勾选「启用 Token」后配额提升到 **5000 次/小时**，并按 Token 独立计数，与代理是否共享无关。

> `/rate_limit` 端点**不计入**配额，所以程序中"测试代理""测试 Token""显示配额"等操作可以放心频繁调用。

## 开发与验证

当前版本：**1.0.1**。使用 .NET 8 SDK 构建：

```powershell
dotnet build GithubReleaseWatch.sln -c Release
dotnet run --project GithubReleaseWatch.Tests
# 需要本机图形环境及 WebView2 Runtime，会短暂打开测试窗口：
dotnet run --project GithubReleaseWatch.Tests -- --browser
```

回归程序使用模拟 HTTP 响应与自身输出目录下的临时配置，不使用真实 Token，也不访问 GitHub。
浏览器测试覆盖长内容、宽度变化、快速往返滚动、内容缩短、卸载重建及版本卡片绑定。
也检查外层滚动后的原生菜单坐标和键盘焦点。`Controls/ScrollAwareWebView2` 包含当前固定 SDK 的位置同步兼容处理，升级 WebView2 包后应运行 `--browser` 回归。
如需检查真实 Ctrl+C，可运行 `dotnet run --project GithubReleaseWatch.Tests -- --input-check`，按测试窗口提示复制选中文本；测试完成后会恢复剪贴板。

## 自动发布

推送 `1.0.1` 或 `v1.0.1` 这样的版本标签后，GitHub Actions 自动校验版本、运行回归检查，并分别打包 Windows x64、x86、ARM64。
每个架构独立生成 ZIP，保留 exe、DLL 和配置文件，**不合并单文件**。

成功后会创建详情为空的 **草稿 Release** 并上传附件，你填写说明后再正式发布。普通代码推送或修改版本号不会触发打包。
操作步骤和重试说明见 [自动发布说明](docs/RELEASING.md)。

## 项目目录

```text
GithubReleaseWatch/
├── App.xaml / App.xaml.cs      # 应用入口
├── Behaviors/                 # 列表拖拽等界面行为
├── Controls/                  # Markdown 浏览器控件与消息解析
├── Models/                    # 配置、仓库与版本数据模型
├── ViewModels/                # 仓库展示状态
├── Services/
│   ├── Configuration/         # 配置读取、原子保存与备份
│   ├── GitHub/                # API、网络会话与缓存
│   └── Markdown/              # GitHub 渲染与 HTML 包装
├── Utilities/                 # 版本比较、应用信息与外部链接
├── Views/
│   ├── Main/                  # 主窗口及按职责拆分的代码
│   ├── Dialogs/               # 共用仓库编辑表单、添加/编辑入口、输入框
│   ├── Settings/              # 设置窗口
│   ├── About/                 # 关于窗口
│   └── Help/                  # 帮助窗口
└── Resources/
    ├── Icons/                 # 应用图标与图片
    └── Defaults/              # 默认配置模板

GithubReleaseWatch.Tests/
├── Program.cs                 # 回归检查入口
├── Domain/                    # 模型、地址、版本与配置检查
├── Networking/                # 模拟 HTTP 与并发检查
└── UI/                        # 窗口和真实 WebView2 检查

assets/icon/                   # 图标生成脚本及导出的多尺寸图标
```

`Views/Main` 下保留按刷新、仓库操作、版本卡片、滚动和配额拆分的主窗口代码。
添加和编辑共用 `Views/Dialogs/RepositoryEditorWindow`。
默认配置在 `Resources/Defaults/config.json`，运行时仍使用 exe 同目录的 `config.json`；构建和发布不会覆盖已有配置。

## 许可协议

**非商业许可**：

- ✅ 可以**修改**源代码
- ✅ 可以**二次创作**（二创）
- ✅ 可以分享给他人（需保留本协议）
- ❌ **不可用于任何商业用途**
- ⚠️ 如需商用，请先联系作者获取书面授权

完整条款见 [LICENSE](LICENSE)。

## 作者

**Boyce Lig**

- GitHub: <https://github.com/BoyceLig/GithubReleaseWatch>
- 机场推荐：<https://github.com/BoyceLig/ProxyRecommendations>

---

Copyright © 2026 Boyce Lig. 非商业许可：可修改、可二创，不可商用。
