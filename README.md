# WSLC 面板工具（wslc-desktop）

为 WSL 容器（[`wslc`](https://learn.microsoft.com/windows/wsl/)）打造的原生 Windows 桌面管理面板。
WinUI 3 + .NET 10，**非打包 + 自包含单文件**分发：目标机无需安装 .NET 运行时，也无需 Windows App SDK 运行时。

## 功能

| 模块 | 内容 |
| --- | --- |
| 容器 | 列表 + 实时 CPU/内存、启动/停止/重启/强杀/删除（全部二次确认）、批量操作、端口一键打开、详情浮层（inspect / 日志快照 / 一次性 exec） |
| 镜像 | 本地表、拉取（实时进度、可取消）、删除、一键用该镜像创建容器 |
| 网络 / 数据卷 / 会话 | 列表与增删（内置网络禁删、删除前写明连带后果） |
| WSL 发行版 | `wsl -l -v` + 注册表 Lxss：状态/版本/默认/安装位置/VHDX 占用、启动/终止/设默认/终端进入/注销 |
| 创建容器 | 完整表单 + **实时等效命令预览**（与真正执行共用同一份拼装逻辑），可从镜像 inspect 预填端口与启动命令 |
| 设置 | 面板偏好 + `settings.yaml` **表单 ↔ 原始 YAML 双模**（逐行替换以保留注释、保存前自动时间戳备份） |
| 事件流 | `wslc events` 长驻订阅驱动即时刷新（断线重连 + 续接去重），轮询降级为兜底 |
| 桌面集成 | 托盘常驻、关闭到托盘、单实例、快捷键 `F5` / `Ctrl+R` / `Ctrl+N` / `Ctrl+1–8`、Mica 与深浅色即时切换 |
| 关于页 | 环境自检（引擎/内核/组件缺失）+ 基于 GitHub Releases 的更新检查 |

## 环境要求

- Windows 10 1809+ / Windows 11
- WSL 容器引擎 **3.0.1 及以上**（`wsl --update`）。版本不足或组件缺失时面板会给出可操作的升级提示
- 构建需要 **.NET 10 SDK**；**运行发布版不需要任何运行时**（自包含）

## 构建与运行

双击或命令行执行仓库根目录的脚本：

```bat
启动.bat            :: 直接启动现有构建产物
启动.bat build      :: 先构建再启动
跑测试.bat          :: 全量测试（含 Live 实机联调）
跑测试.bat unit     :: 跳过 Live 实机联调，更快
```

手动命令：

```bat
dotnet build WSLC-Panel.slnx -t:Rebuild
dotnet test  tests\WSLC.Panel.Core.Tests\WSLC.Panel.Core.Tests.csproj
```

> `启动.bat` / `跑测试.bat` 必须保持 **GBK(ANSI) 编码 + CRLF 换行**：cmd 按代码页 936 解析批处理，
> 存成 UTF-8 会让中文变乱码字节、甚至拆散注释行导致后续语句被当命令执行。`.gitattributes`
> 已经把这两个文件钉死在 CRLF，编码仍需编辑器里显式选择。

> **不要用 `dotnet run`**：本项目是非打包 WinUI 3 应用，`Microsoft.Windows.SDK.BuildTools.WinApp`
> 的 Run 钩子会按"打包应用"启动，未开启开发者模式时直接报
> `Failed to launch application: Developer Mode is not enabled`。请构建后直接运行 exe。

## 目录结构

```
src/WSLC.Panel.Core/      服务层：wslc CLI 通道、模型、解析器（纯函数 + fixtures 单测）
src/WSLC.Panel.App/       WinUI 3 界面：10 个页面、详情浮层、托盘、单实例
tests/WSLC.Panel.Core.Tests/   xUnit：解析/拼命令/事件流/偏好/更新检查 + Live 实机联调
```

## 测试基线

- 单元测试 + `Live*` 实机联调必须全绿（`Live*` 在引擎不可用时自行跳过，不会让套件变红）
- 渲染 / 布局 / 真实交互必须本机实测：CI 查不出这类问题

## 已知边界

- 官方 `wslc compose` 尚未发布，本面板不自研编排层
- 交互式终端与 `logs -f` 跟随交给 Windows Terminal（`wt`），不可用时退回控制台窗口
- 更新检查只做「比对 + 给出下载落点」，不做自动下载替换（非 MSIX 自更新要处理占用中的 exe、权限与签名，风险大于收益）
- 拉取进度：当前引擎只返回状态关键字（不返回字节数），进度条为不确定态 + 状态文本；引擎日后给出字节数会自动切百分比

## 许可证

[Apache-2.0](LICENSE)。
