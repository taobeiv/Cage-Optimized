# Cage Optimized · Windows 个人优化版

基于 [Lee-xeo/Claude-Cage](https://github.com/Lee-xeo/Claude-Cage) 的个人优化源码快照，保留上游许可证与版权说明。不是上游官方发行版。

## 本版内容

- 中文输入与点击响应优化。
- 滚轮输入、鼠标捕获和大窗口显示适配。
- Windows 系统授权和应用提示窗口：显示真实系统界面，由用户自行允许或取消，处理后返回 Cage。
- 项目共享盘符稳定性与挂载恢复。
- Windows 虚拟机保存状态恢复，以及启动时系统检查超时修复。

系统弹窗按已识别的类型适配，并非承诺覆盖所有应用。文件选择和自动弹出的登录／锁屏界面不属于这次适配范围。需要管理员密码的操作仍由用户在 Windows 界面中确认。

## 获取与使用

```powershell
git clone https://github.com/taobeiv/Cage-Optimized.git
cd Cage-Optimized
```

**当前提供源码，没有附带可直接安装的 Release 安装包。** 拉取源码后需要构建，不能仅复制源码中的某个文件来完成安装。

按 [Windows 构建说明](docs/windows.md#windows-源码构建) 准备 PowerShell 7、Go、Node.js、锁定版本的 .NET SDK、uv、7-Zip 和 MSYS2 等工具，然后在仓库根目录运行：

```powershell
.\scripts\build-windows.ps1
```

此命令构建主程序和随附客机组件。需要安装器时，继续按上述说明运行 `scripts/package-windows.ps1`。默认是未签名开发构建，需手动安装和更新。

首次使用请创建自己的 Cage 环境，使用自己的 Claude 账号、代理和 Windows 管理员凭据。已有 Cage 用户先保存任务、停止环境并退出，再升级程序；客机组件从“设置 → 环境与恢复”更新。Windows 客机需要满足 [客机系统要求](docs/guide/guest-systems.md)。

## 来源和隐私

上游源码基线：`fb11cde1a7186b844f23d710dd6ae165849fbaa2`，加上本仓库的本地优化。为避免携带本地开发记录，本仓库从一条新的提交历史开始，原作者版权及依赖许可证继续保留。

仓库仅发布程序源码、通用测试与文档。不包含个人 Cage 配置、账号会话、代理订阅、认证密钥、虚拟机磁盘、聊天记录、诊断日志或本机安装目录。

完整上游介绍见 [README.upstream.md](README.upstream.md)，许可证见 [LICENSE](LICENSE)。
