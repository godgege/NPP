# Netch Lite — 进程级代理转发工具

## 核心功能
输入本地 SOCKS5 代理地址（Host + Port），选择需要代理的进程，启动后对选中进程进行流量转发。

## 已完成

### 2026-07-01

- **WinUI 3 启动修复**
  - App.xaml 补充 `<XamlControlsResources />` 主题资源（解决 NavigationView 内部样式缺失）
  - Program.cs 去掉单实例检测逻辑（AppInstance 残留导致秒退）

- **系统应用发现服务 (`AppDiscoveryService`)**
  - 从注册表 Uninstall 键（含 WOW6432Node）读取已安装程序
  - 从开始菜单快捷方式（.lnk）补充发现
  - 按安装目录去重，过滤系统目录
  - 安全遍历目录（跳过无权限路径）

- **主界面双栏布局**
  - 左栏：搜索框 + 系统已安装应用列表（CheckBox 勾选）
  - 右栏：已选应用分组，每组显示扫描到的所有 exe（逗号分隔）
  - 每组配有 "+" 按钮，可继续添加目录补充 exe
  - 每组配有 "×" 按钮移除

- **数据模型**
  - `InstalledApp` — 系统已安装应用（Name、InstallPath、IsSelected）
  - `ProcessGroup` — 选中的应用分组（GroupName、Processes、DisplayText）
  - `ProcessEntry` — 单个 exe 条目（FullPath、FileName）

- **持久化**
  - 代理 Host/Port 和已选应用路径保存到 `data/settings.json`（`SelectedAppPaths`、`LiteProxyHost`、`LiteProxyPort`）
  - 下次启动自动恢复选中状态

- **端口可用性检测**
  - 启动前 TCP 连接测试代理端口，不通则提示用户

- **手动添加 exe**
  - 每个分组右侧额外加文件选择按钮，可单独添加 exe 文件

- **托盘图标 + 最小化到托盘**
  - 使用 H.NotifyIcon.WinUI 实现系统托盘图标
  - 关闭窗口时最小化到托盘（除非设置了"关闭时退出"）
  - 托盘右键菜单：Show / Exit

- **开机自启**
  - Settings 页面 "Run at startup" 开关写入 `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run`

- **国际化 (i18n)**
  - 使用 WinUI 3 标准 .resw 资源文件（`Strings/en-US/` + `Strings/zh-CN/`）
  - MainPage、MainWindow 导航栏使用 `x:Uid` 绑定
  - 自动跟随系统语言

- **UI 美化**
  - 主题切换：Settings 里 System/Light/Dark 三档选择
  - 右栏分组使用更清晰的图标（文件夹、添加、删除）
  - 底栏增加分隔线，状态文字使用次级颜色
  - 右栏占比加大（1.2*），exe 列表字号缩小（12px）便于阅读

- **运行日志面板**
  - MainPage 底部可折叠的 Log 面板（Expander）
  - 最多显示 1000 条日志，等宽字体，带时间戳
  - 通过自定义 Serilog Sink（`UiLogSink`）实时从内核日志推送到 UI
  - 启动/停止/错误等关键操作记录日志

## 待完成

- [ ] 更多语言支持（日语、繁体中文等）暂时不需要，只要中文即可
