# FRP 直连开机启动模板

这些是通用模板，不含服务器地址、FRP 认证令牌、Windows 密码或本机任务备份。实际 `frpc.toml` 留在自己的电脑上。

`start-frpc-direct.cmd` 放在 `frpc.exe` 和 `frpc.toml` 同一目录。只清除当前脚本及子进程的 HTTP_PROXY / HTTPS_PROXY / ALL_PROXY，使用绝对路径启动客户端，日志留在本机；不修改全局代理。

若尚未配置启动任务，在管理员 PowerShell 中运行：

```powershell
.\Install-FrpStartup.ps1 -FrpDirectory 'C:\tools\frp'
```

默认建立“开启FRP”任务，SYSTEM 账户，系统开机触发，无需 Windows 登录。失败每 1 分钟最多重试 3 次。监控软件的“启动 FRP”按钮调用该任务。

脚本遇到同名现有任务会停止，不覆盖已有配置。不创建每天重启任务，不启动或关闭当前 FRP，也不修改 `frpc.toml`。已有启动任务保持原样时，无需运行此安装模板。

“已启动”仅表示检测到 frpc.exe。云端登录与隧道注册状态查看本机 `frpc-startup.log`。网络长时间不可用时，三次重试可能耗尽；本模板不是无限重试服务。

