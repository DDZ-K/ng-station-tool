# NgStationTool · NG 工位流转中心

Windows 10 x64 · .NET 8 WinForms 自包含程序。

## 当前版本

**v1.5.1** — 修复：拷贝成功却不入「待 NG」队列、XML identifier 明明一致却报「不在待NG队列」。

## 当前流程

```text
图片源目录\产品DMC文件夹\图片
          ↓ 整夹静默后改名
A 目录（待 NG 队列）  ← 拷贝成功即入队，显示完整图片名 + 产品DMC
          ↓ XML 中 partReceived@identifier 匹配产品文件夹名（允许 _S1 等后缀）
B 目录\年\月\日（待判断队列）
          ↓ Log 文件名包含完整图片名，正文含合法 OK/NOK
模拟 9/7；同产品全部图片完成后延迟 Enter
```

### 两种 DMC

- **产品 DMC**：图片所在一级产品文件夹名，用于匹配 XML 的 `identifier`（也支持 `identifier_S1` 这类站位后缀文件夹）。
- **待判断图片名**：改名后的完整图片文件名（无扩展名），显示在两个队列中，并用于匹配云端 Log 文件名。

### XML 报文归档

- identifier 命中待 NG 产品：报文移入 `报文归档\已匹配`。
- identifier 不在待 NG 队列、XML 无 identifier 或无法解析：报文移入 `报文归档\未匹配`。

### 队列规则

- 没有 DMC 等待超时。
- 没有 wait/HARAN 图片比对。
- A 目录不按日期分层；B 目录按 `年\月\日` 分层。
- 托盘：关闭/收起=隐藏继续监控；仅「退出程序」结束。

## 默认测试目录

| 用途 | 默认路径 |
|---|---|
| 图片源与 XML 报文 | `E:\Download\AI\Test` |
| A · 待 NG | `E:\Download\AI\TestA` |
| B · 待判断 | `E:\Download\AI\TestB` |
| XML 归档 | `E:\Download\AI\Test\报文归档` |
| 云端 Log | `E:\Download\AI\CloudResult` |

## 构建与自检

```bat
dotnet build NgStationTool\NgStationTool.csproj -c Release
dotnet run --project NgStationTool\NgStationTool.csproj -c Release -- --self-test
```
