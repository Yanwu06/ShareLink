# ShareLink Windows 1.1.0

## 相比 1.0.0 的更新

- 更新 Windows 界面的功能图标。分享模式、设备、文件操作、连接说明和二维码占位图标统一使用开源的 Lucide 1.17.0。应用继续使用原创的 ShareLink 标志。
- 修复设备互传时，连续发送或接收大量小文件，速率容易一直显示为 0 的问题。速率按整批传输中的新增字节计算，逐文件校验期间继续使用同一份速率采样。

## 下载与升级

适用于 Windows 10 2004 及以上版本和 Windows 11 的 x64 设备。安装包内含运行环境。

已安装 1.0.0 的用户可在「设置 → 检查更新」中打开新版下载链接，也可直接下载本页的 `ShareLink-Setup-x64.exe`。下载后先退出 ShareLink，再运行安装包覆盖安装。安装器默认沿用原安装目录。

对比基准为 Gitee 上已发布的 Windows 1.0.0。[1.0.0 发行版](https://gitee.com/yanwu06/win-sharelink-transfer-release/releases/tag/v1.0.0)

## 源码与安装包

两个平台提供相同的 Windows 安装包、完整源码压缩包和 SHA256 校验文件。[GitHub 发行版](https://github.com/Yanwu06/ShareLink/releases/tag/v1.1.0) · [Gitee 发行版](https://gitee.com/yanwu06/win-sharelink-transfer-release/releases/tag/v1.1.0)

完整源码附件名为 `ShareLink-1.1.0-source.zip`，对应 Windows 1.1.0 的发行提交 `9640fcf`。下载后可用 `SHA256SUMS.txt` 核对安装包和源码附件。
