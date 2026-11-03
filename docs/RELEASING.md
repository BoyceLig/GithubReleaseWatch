# 自动发布

工作流：`.github/workflows/release.yml`。

## 触发方式

**仅推送版本标签时触发**，同时支持 `1.0.1` 和 `v1.0.1` 两种格式。普通分支推送、Pull Request 或单独修改版本号均不会触发。

1. 修改 `GithubReleaseWatch/GithubReleaseWatch.csproj` 中的 `<Version>`，例如 `1.0.2`。
2. 提交并推送代码，确保该提交包含发布工作流和 `scripts/release/` 脚本。
3. 在需要发布的提交上创建标签并推送：

   ```powershell
   git tag v1.0.2
   git push origin v1.0.2
   ```

标签必须与项目版本一致；不一致会停止构建。预发布版本也支持，例如项目版本 `1.0.2-beta.1` 对应标签 `v1.0.2-beta.1`。

## 工作流执行内容

1. 校验标签、还原依赖、构建解决方案，运行常规回归检查和发布脚本测试。
2. 分别为 `win-x64`、`win-x86`、`win-arm64` 发布完整程序目录，校验 exe 和原生 WebView2 加载器的架构。
3. 每个架构生成 ZIP 和 SHA-256 校验文件。
4. 三个架构全部成功后，创建 **草稿 Release**，上传六个附件；标题默认为版本名称，详情留空。
5. 进入仓库 **Releases**，编辑草稿、填写更新说明，最后点击 **Publish release**。

例如标签 `v1.0.2` 的附件为：

```text
GithubReleaseWatch-v1.0.2-win-x64.zip
GithubReleaseWatch-v1.0.2-win-x64.zip.sha256
GithubReleaseWatch-v1.0.2-win-x86.zip
GithubReleaseWatch-v1.0.2-win-x86.zip.sha256
GithubReleaseWatch-v1.0.2-win-arm64.zip
GithubReleaseWatch-v1.0.2-win-arm64.zip.sha256
```

## 打包方式

- 使用目录发布，**不合并单文件**，DLL 与 exe 一同放入 ZIP。
- 不携带 .NET Runtime；使用者需要对应架构的 **.NET 8 Desktop Runtime** 和 **WebView2 Runtime**。
- 包内配置来自 `Resources/Defaults/config.json`，不使用开发机输出目录中的配置；模板包含 Token 或代理认证信息时会停止打包。
- 排除 XML API 文档和 PDB 调试符号，保留运行所需的 DLL、图标、默认配置及许可证。
- CI 不运行 `--browser` 图形测试；真实浏览器回归仍可在本机交互桌面运行。

## 重试与权限

工作流使用 GitHub 自动提供的 `GITHUB_TOKEN`，不需要额外配置个人 Token。只有创建草稿的任务申请 `contents: write` 权限。

失败后可以在 Actions 中重新运行：已有草稿的同名发布附件会重新上传，已填写的标题、说明和其他附件保留。**已正式发布的 Release 不会被自动修改。**

## 本地验证

```powershell
./scripts/release/Get-ReleaseVersion.ps1 -Tag v1.0.1
./scripts/release/Build-Package.ps1 -Tag v1.0.1 -RuntimeIdentifier win-x64
./scripts/release/Build-Package.ps1 -Tag v1.0.1 -RuntimeIdentifier win-x86
./scripts/release/Build-Package.ps1 -Tag v1.0.1 -RuntimeIdentifier win-arm64
node --test scripts/release/Create-Draft.test.cjs
```

请把示例标签替换为当前项目版本。压缩包输出到 `artifacts/releases/`；本地打包命令不会访问 GitHub 创建 Release。
