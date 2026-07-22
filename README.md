# NgStationTool · 工位图片命名 + 云端 log 放行

Windows 10 x64 桌面工具（.NET 8 WinForms，**自包含 exe，目标机可不装 .NET**）。

当前版本：**v1.3.5**

在原先「文件夹图片监控改名/拷贝」能力之上，合并：

- **整夹批处理**改名输出  
- **DMC 待确认缓存**  
- **云端 log 匹配（文件名包含 DMC）**  
- **模拟按键放行**（小键盘 / Home / PageUp 等可配）  
- **OK / NOK / 超时归档**  
- **HARAN Waiting 门闩**（先改名暂存 → 有待办才轮询 wait → Out/入DMC）  
- **A/B 双片串行会话**（前组 9/7+回车并离开 Waiting 后再处理下一组）

本仓库由旧版 `folder-image-renamer`（PowerShell 图片监控）**升级替换**而来。

---

## 功能概览

| 模块 | 作用 |
|------|------|
| 图片命名 | 监视 `WatchRoot` 下一级子文件夹内图片；**静默等齐后一次性**复制/暂存，名为 `子文件夹_原文件名`；源文件不动 |
| HARAN 门闩 | 门闩开：先暂存；**有暂存/待判定才**截图找 Waiting；匹配后再 Out + 入 DMC |
| 会话串行 | A/B 重叠最多两片：同一时刻只放行一个文件夹组；前组结束+离开 Waiting+延迟后再开下一组 |
| DMC 缓存 | 每张成功 Out 入一条：DMC = **改名后完整文件名（无扩展名）** |
| 云端放行 | 监视 log 目录；**文件名包含**缓存中的 DMC；内容 OK/NOK → 按配置键 |
| 归档 | OK / NOK / **超时**均归档 |

---

## 产线流程（简图，门闩开启）

```text
监控目录\产品夹\ 多张图
        ↓ 静默后改名暂存（不进 Out、不入 DMC）
有暂存后 → 截图匹配 Waiting for Input
        ↓
输出目录\…  产品夹_图1.jpg …  + DMC 入缓存
        ↓ 云端 log
模拟 9/7（可组内全部完成后再回车）
        ↓ 整组结束 + 界面离开 Waiting + 延迟
下一组暂存才允许 Out
```

---

## 直接运行（公司电脑 / Win10）

1. 下载 [Releases](https://github.com/DDZ-K/ng-station-tool/releases) 里最新 zip  
2. 解压后双击 **`NgStationTool.exe`**  
3. **配置**改公司路径；模板放 `haran-templates\waiting\`（可用 HaranUiProbe 录）  
4. 点 **开始**（初始应显示 HARAN=**待命**，有图暂存后才轮询）

### 不需要

- 安装 .NET Runtime（自包含发布）  
- Visual Studio / PowerShell 脚本环境  

### 需要

- **Windows 10/11 x64**  
- 可写的监控盘、输出盘、log 盘  
- 若设备软件以管理员运行，本工具也建议管理员运行（否则按键可能无效）  

---

## 从源码构建

```bat
cd NgStationTool
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o ..\publish
```

自检 / 仿真：

```bat
dotnet run -c Release -- --self-test
dotnet run -c Release -- --sim-serial
dotnet run -c Release -- --sim-delayed-wait 60
```

---

## 配置说明

界面 **配置…** 分四页：

| 标签 | 内容 |
|------|------|
| ① 通用 | 图片命名 / 云端放行独立开关 |
| ② 图片命名 | WatchRoot、OutputRoot、日期归档等 |
| ③ 云端 log 放行 | log 目录、DMC 入库、OK/NOK 词、按键、窗口标题 |
| ④ 时序 | 静默、就绪、超时等毫秒/秒参数 |

### 故意拖延「改名后出现在输出目录」

主要调大 **`FolderSettleMs`（整夹静默）**——最后一张新图后再等多久才一次性拷出。  
`BatchMaxWaitMs` 是等图写完的上限，不是故意拖延输出的主旋钮。

### 按键

配置里可写例如：

- `NumPad9` / `NumPad7`（小键盘）  
- `Home` / `PageUp`（或 `PgUp`）等导航键  

默认打到**当前前台窗口**；可填窗口标题关键字先激活设备软件。

更细的大白话说明见：

- `docs/工位工具_配置参数说明_大白话.pdf`  
- 或 `publish` 目录下同名 PDF  

改监视路径后需 **停止 → 开始** 才生效。

---

## 仓库结构

```text
ng-station-tool/
  NgStationTool/     # C# 源码
  docs/              # 说明 PDF
  publish/           # 可选：本地构建产物（大 exe 默认不强制提交，见 .gitignore）
  README.md
```

`config.json` 请用本机路径；可用程序首次运行自动生成，或参考界面保存结果。

---

## 与旧版 PowerShell 的关系

| 旧 `folder-image-renamer` | 本工具 |
|---------------------------|--------|
| 仅图片监控复制命名 | 命名 + DMC + 云端 log + 按键 + GUI |
| `.ps1` + 静默 VBS | 单文件 exe + 托盘 |

产线请只跑 **一个** 监视端，避免双开抢同一目录。

---

## 稳定性说明（简要）

- 事件入队后台处理、单实例、单文件失败尽量不拖垮进程  
- **不能保证 0 闪退**；合盖休眠、杀毒拦截、权限不足更常见于「功能异常」而非天天崩溃  
- 重要放行建议保留人工兜底意识  

---

## 许可证

MIT
