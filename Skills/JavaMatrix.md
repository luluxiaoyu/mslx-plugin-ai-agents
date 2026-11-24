# MC 与 Java 版本对应准则 - 必须严格遵守

根据 Minecraft 游戏核心及服务端核心（Forge / NeoForge / Fabric / Paper / Spigot 等）的版本，严格匹配相应的 Java 运行环境：

- **NeoForge 26.x / MC 26.1+** ➔ 必须匹配 Java 25 (`MSLX://Java/25`)
- **MC 1.20.5 - 1.21.11** ➔ 推荐匹配 Java 21 (`MSLX://Java/21`)
- **MC 1.18 - 1.20.4** ➔ 推荐匹配 Java 17 (`MSLX://Java/17`)
- **MC 1.17 / 1.17.1** ➔ 推荐匹配 Java 16 (`MSLX://Java/16`)
- **MC 1.13 - 1.16.5** ➔ 推荐匹配 Java 11 (`MSLX://Java/11`) 或 Java 8 (`MSLX://Java/8`)
- **MC 1.12.2 及更低旧版本** ➔ 必须匹配 Java 8 (`MSLX://Java/8`)

## Java 诊断与自动安装核心准则（必须严格遵守）

1. **排查故障与查询 Java 时优先强制全盘重扫 (`force_rescan: true`)**：
   - 当遇到服务器启动失败（如进程拉起后立即退出、无日志生成）、或者查询到的本地 Java 版本与 MC 所需不匹配时，【必须首先调用 `query_java_environments(force_rescan: true)`】进行全盘强制重新扫描！
   - 因为用户很可能已经在宿主机本地安装了新版 JDK，但 MSLX 之前的旧缓存尚未刷新。

2. **绝对严禁教用户手动去终端安装 JDK**：
   - 严禁在回复中输出 `brew install openjdk@25`、`apt install`、`winget` 等终端命令，或发送官网链接要求用户手动去终端下载安装！
   - MSLX 拥有完全内置的【官方在线 Java 安装】体系！

3. **缺少匹配 Java 时的全自动处理闭环**：
   - 若强制全盘重新扫描后主机确实未安装目标 Java 版本，直接果断调用 `install_online_java(version: X)` 工具进行后台一键下载与自动部署！
   - 或者直接调用 `update_instance_settings(server_id: X, java: "MSLX://Java/25")`，系统会自动拉取镜像源部署该 Java 环境并自动绑定到实例。
   - 部署完成后直接调用 `control_server(server_id: X, action: "start")` 启动服务器，整个过程全自动闭环，完全无需用户动手！

4. **开服时自动部署**：
   - 开服工具 `create_mc_server` 内部已深度集成 MSLX 在线 Java 机制，若本地全盘未找到对应版本，系统会自动挂载 `MSLX://Java/{major}` 并在后台静默下载部署，绝对不可为了妥协而盲目绑定低版本 Java（如用 Java 21 运行 MC 26 会直接导致启动崩溃）！

5. **切换 Java 指令**：
   - 当用户要求“把 Java 切换为合适的版本”或修改设置时，绝对禁止反问用户或询问“你想用 Java 21 还是 25”，必须根据上述规则立即调用 `update_instance_settings` 工具执行切换！
