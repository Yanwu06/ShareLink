# 第三方声明

## Inno Setup 简体中文翻译

`installer/languages/ChineseSimplified.isl` 与 Inno Setup `is-6_7_3` 标签下的文件字节一致，保留了译者署名。中文翻译上游采用 MIT，原始 Inno Setup 软件使用其自有许可。相关文本随此项目保存。[译文上游](https://github.com/kira-96/Inno-Setup-Chinese-Simplified-Translation) · [Inno Setup 固定版本](https://github.com/jrsoftware/issrc/blob/is-6_7_3/Files/Languages/Unofficial/ChineseSimplified.isl) · [译文许可](installer/languages/Chinese-translation-license.txt) · [Inno Setup 许可](installer/languages/Inno-Setup-license.txt)

## 构建依赖

Windows 浏览器分享工程通过 NuGet 使用 QRCoder。Rust 依赖及固定版本见 `Cargo.lock`。Inno Setup 编译器按其授权从上游安装。[QRCoder](https://www.nuget.org/packages/QRCoder/1.7.0) · [Rust 依赖锁文件](Cargo.lock)

## Lucide 图标

Windows 功能图标源自 Lucide 1.17.0，并转换为 WPF 矢量资源。Lucide 原创图标采用 ISC，源自 Feather 的图标采用 MIT。完整的版权及许可文本随源码、Windows 构建产物和安装器提供。[Lucide 固定版本](https://github.com/lucide-icons/lucide/tree/1.17.0) · [完整许可](windows/ShareLink.BrowserSharing/Lucide.LICENSE.txt)
