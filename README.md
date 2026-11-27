# NPP

<p align="center">
  <img src="NPP/Assets/NPP.png" width="96" alt="NPP icon" />
</p>

NPP 是一个简洁的 Windows 进程级代理工具。它把能归属到选定进程的 TCP、UDP 及进程自行发出的 DNS 流量转发到一个 SOCKS5 入口，其他进程保持直连。

内置完整的 v2rayN 绿色版，并在独立的“代理节点”标签页中嵌入原生界面。节点、订阅、路由和内核设置沿用 v2rayN 自带的操作和默认值。“启动时连接节点”默认开启，会随 NPP 启动 v2rayN 并加载已选节点。关闭后可以在该标签页手动启动。真正退出 NPP 时会一并停止分流和 v2rayN。

## 使用

1. 运行 `NPP.exe`；进程流量拦截需要管理员权限。
2. 在“代理节点”标签页导入节点或订阅并选择节点。需要为选中的应用转发流量时，在“应用分流”页填写本地 SOCKS5 地址和端口（默认 `127.0.0.1:10808`）。也可填写已有外部 SOCKS5 代理。
3. 从“应用列表”选择应用，或用“目录查找”扫描可执行程序。
4. 点击“检测连通”旁的“已停止 · 启动”开启分流，点击“运行中 · 停止”关闭分流。“启动时开启分流”默认开启，适用于手动打开和开机自启，已有设置采用之前保存的值。应用分流和代理节点可以分别启动。分流状态显示规则是否启用。SOCKS5 入口就绪前，走分流的请求会失败。入口就绪后，应用发起的新请求即可转发。

托盘图标右下角的绿色圆点表示代理已开启，红色圆点表示代理未开启；悬停可查看当前状态。

NPP 打开时自动检测“应用分流”页填写的 SOCKS5 入口。启动自检最多持续 20 秒，失败后间隔 2 秒重试，首次连通即停止。从托盘或快捷方式重新打开已有窗口时，会重新检测一次。结果和耗时显示在 SOCKS5 配置区；也可随时点击“高级”旁的“检测连通”重测。更改地址或端口会清除旧结果。

检测先完成无认证 SOCKS5 握手，再经该代理访问 `https://www.gstatic.com/generate_204`，失败时尝试 `https://cp.cloudflare.com/generate_204`。SOCKS5 配置区的圆点表示最近一次检测结果，检测完成后关闭连接。

为保持严格的进程隔离，NPP 不会接管 Windows 共享 DNS Client 服务的全局查询。应用自行发送的 DNS 请求会使用高级设置中的 DNS 服务器；委托给系统服务的查询仍由系统解析，避免把其他进程的 DNS 一并代理。

开启“绕过局域网”时，代理 DNS 使用的 `198.18.0.0/15` 虚拟地址仍会按应用规则分流。

当前版本只配置一个代理入口。若未来需要为不同应用使用不同端口，可在此基础上扩展为多标签页。

## 构建

Visual Studio 入口为 `NPP.sln`，其中应用和核心工程分别为 `NPP\NPP.csproj` 与 `NPP.Core\NPP.Core.csproj`。

首次使用 Visual Studio 构建前，准备客户端资源（正式打包脚本会自动执行此步骤）：

```powershell
.\prepare-v2rayn.ps1
# 也可使用已下载的同版本官方绿色包，脚本会校验 SHA256：
.\prepare-v2rayn.ps1 -ArchivePath 'C:\path\v2rayN-windows-64-desktop-aot.zip'
```

客户端分发资源位于 `Storage\v2rayN\`，构建时完整复制到 `clients\v2rayN\`。版本、下载地址和校验和记录在 `Storage\v2rayN.package.json`；二进制不提交到 Git。运行时使用 NPP 用户数据目录下的 `clients\v2rayN\` 独立副本，节点、订阅和设置保存在其中的 `guiConfigs\`，NPP 更新不会替换这些用户数据。不要直接运行 `Storage` 中的资源副本。

```powershell
.\build-npp.ps1 -Configuration Release -OutputPath release -Version 1.0.0
```

输出的整个 `release\` 目录就是免安装版，保留完整目录结构，直接运行其中的 `NPP.exe`。随附的 .NET/WinUI 运行库、`Redirector.bin`、`nfapi.dll`、`bin\nfdriver.sys` 和 `clients\v2rayN\` 都需一起分发。

构建结束会将最新版同步到固定的 `velopack\NPP-Portable\`，后续始终覆盖该目录，不再按功能创建不同名称的文件夹。也可单独运行 `.\update-portable.ps1` 同步已有的 `release\`。更新前请从沙盒托盘正常退出 NPP，释放 DLL；无需关闭沙盒。

连通检测的本地模拟验证可运行 `dotnet run --project Tests\ConnectivityChecks\ConnectivityChecks.csproj -c Release`，无需真实代理节点。

## 沙盒验证（免安装）

在本机双击 `NPP-Sandbox.wsb`，配置会以只读方式将最新 `release\` 映射到沙盒桌面的 `NPP-Portable` 文件夹，并在资源管理器中选中目录内的 `NPP-Portable.lnk`。双击快捷方式即可运行，无需查找 EXE 或安装 MSI。修改共享配置后，需关闭原沙盒并重新打开该 `.wsb` 才会采用新的映射。

对于已经打开且仍使用 `NPP-Package` 共享目录的沙盒，刷新文件夹，双击共享目录顶层的 `NPP-Portable.lnk` 即可。固定的 `NPP-Portable\` 文件夹内也有同名入口；不会创建桌面快捷方式或启动脚本。

## Velopack 打包与更新

本地生成安装包、便携包和更新包：

```powershell
.\build-velopack.ps1 -Configuration Release -Version 1.0.0
```

生成结果位于 `velopack\`：

- `NPP-stable.msi`：正式安装版，按机器安装到 `Program Files\NPP`，安装时必须通过管理员权限确认。
- `NPP-stable-Portable.zip`：便携版。
- `NPP-1.0.0-stable-full.nupkg`：完整更新包。
- `releases.stable.json`：更新索引。

打包脚本会校验 MSI 的 `ALLUSERS=1`，并移除 Velopack 同时生成的按用户 `Setup.exe`，防止误发不具备管理员安装范围的安装器。

通过 MSI 安装的版本可在“设置 → 更新”中检查、下载并重启应用更新。便携版或直接从调试输出目录运行的版本不受 Velopack 安装管理，更新区会明确显示不可用。

GitHub Actions 在推送新的 SemVer 标签后按 `vpk download github → vpk pack → vpk upload github` 发布；`1.0.0` 是 NPP 的首个正式版本，从第二个版本开始会自动利用 `stable` 渠道的上一 full 包生成 delta。

## 发布注意

正式分发使用管理员 MSI；应用本体也会请求管理员权限。发布前应为应用、MSI 和更新包配置可信 Windows 代码签名，并在干净系统上验证安装、更新、便携运行与卸载。

## License

NPP 基于 Netch 项目演进，遵循 [GPLv3](LICENSE)。

随附的 [v2rayN](https://github.com/2dust/v2rayN) 遵循 GPLv3，许可证位于 `Storage/v2rayN.LICENSE`，对应源码地址记录在 `Storage/v2rayN.package.json`。随绿色包附带的各内核及数据文件保留其上游许可。
