# NPP

<p align="center">
  <img src="NPP/Assets/NPP.png" width="96" alt="NPP icon" />
</p>

NPP 是一个简洁的 Windows 进程级代理工具。它把能归属到选定进程的 TCP、UDP 及进程自行发出的 DNS 流量转发到一个 SOCKS5 入口，其他进程保持直连。

## 使用

1. 运行 `NPP.exe`；进程流量拦截需要管理员权限。
2. 填写 SOCKS5 地址和端口；代理在本机时勾选“本机地址”。
3. 从“应用列表”选择应用，或用“目录查找”扫描可执行程序。
4. 点击标题栏的“启动代理”；再次点击即可停止。

为保持严格的进程隔离，NPP 不会接管 Windows 共享 DNS Client 服务的全局查询。应用自行发送的 DNS 请求会使用高级设置中的 DNS 服务器；委托给系统服务的查询仍由系统解析，避免把其他进程的 DNS 一并代理。

当前版本只配置一个代理入口。若未来需要为不同应用使用不同端口，可在此基础上扩展为多标签页。

## 构建

Visual Studio 入口为 `NPP.sln`，其中应用和核心工程分别为 `NPP\NPP.csproj` 与 `NPP.Core\NPP.Core.csproj`。

```powershell
.\build-npp.ps1 -Configuration Release -OutputPath release -Version 1.0.0
```

输出目录中的 `NPP.exe` 是应用入口。`Redirector.bin`、`nfapi.dll` 和 `bin\nfdriver.sys` 必须随应用一起分发。

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
