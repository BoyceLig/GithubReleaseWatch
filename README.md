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
- **托盘常驻** —— **打开软件即驻留系统托盘**（不必先关窗口），关闭窗口最小化到托盘，双击图标恢复窗口，右键可刷新/设置/退出

## 环境要求

- Windows 10 / 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（含 ".NET 桌面开发" 工作负载）
- Visual Studio 2022（可选，推荐）

## 运行方式

下载并运行Releases最新版的exe

## 配置文件

程序首次运行会在 **exe 同目录**生成 `config.json`，结构如下：

```json
{
  "Repos": [
    {
      "Url": "https://github.com/BoyceLig/GithubReleaseWatch",
      "Owner": "BoyceLig",
      "Repo": "GithubReleaseWatch",
      "CurrentVersion": "v1.0.0"
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

---

Copyright © 2026 Boyce Lig. 非商业许可：可修改、可二创，不可商用。
