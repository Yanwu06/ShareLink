# ShareLink

ShareLink 是近距离文件分享应用。本仓库提供 Windows 客户端及其传输核心。双方安装 ShareLink 时，可以用蓝牙发现设备，确认连接后通过临时 Wi-Fi 双向收发文件。浏览器分享由一方发起，另一方可通过网页下载，发起方允许上传后也可通过网页传回文件。

当前源码版本为 Windows **1.1.0**。[更新说明](docs/RELEASE-1.1.0.md) · [Windows 安装包](https://gitee.com/yanwu06/win-sharelink-transfer-release/releases)

## 工程结构

| 目录 | 内容 |
| --- | --- |
| `windows/` | WPF 客户端、浏览器分享服务和诊断工程 |
| `crates/core/` | 共享 Rust 传输核心和命令行调试程序 |
| `shared/` | 浏览器分享页面资源 |
| `installer/` | Windows Inno Setup 安装器工程 |
| `scripts/`、`tests/` | 构建脚本、核心集成测试和 Windows 更新检查测试 |

## Windows 构建

在 x64 Windows 上安装 PowerShell 7、.NET 10 SDK、带 MSVC 和 Windows SDK 的 Visual Studio C++ 生成工具，以及 Rust 的 `x86_64-pc-windows-msvc` 工具链。打包安装器还需 Inno Setup 6.7 或更新的兼容版本。[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) · [Visual Studio 生成工具](https://visualstudio.microsoft.com/downloads/) · [Rust 工具链](https://rustup.rs/) · [Inno Setup](https://jrsoftware.org/isinfo.php)

Windows 功能图标随仓库提供。从仓库根目录运行以下命令。[界面资源说明](docs/INTERFACE-ASSETS.md)

```powershell
.\scripts\build-windows.ps1
.\scripts\build-windows-installer.ps1
```

运行文件在 `artifacts/windows-1.0.0/`，请保留 `ShareLink.exe` 同目录下的运行时与 `sharelink_core.dll`。安装器和校验清单在 `artifacts/installers/1.0.0/`。Inno Setup 安装在自定义目录时，可给第二条命令传入 `-CompilerPath` 和 `ISCC.exe` 的完整路径。

## 测试

```powershell
cargo test --workspace --locked -- --test-threads=1
dotnet run --project tests/ShareLink.UpdateChecks/ShareLink.UpdateChecks.csproj -c Release
```

本机收发集成测试可在 `cargo build --release --locked` 后运行 `python tests/integration_cli.py target/release/sharelink-cli.exe`。

## 发行与许可证

Windows 安装包和应用内检查更新使用 Gitee 发行渠道。鸿蒙版请在华为应用市场搜索 ShareLink。[Windows 发行版](https://gitee.com/yanwu06/win-sharelink-transfer-release/releases)

ShareLink 原创代码按 MIT 许可证发布，署名 `yanwu06`。第三方内容沿用各自许可证。[MIT 许可证](LICENSE) · [第三方声明](THIRD_PARTY_NOTICES.md)
