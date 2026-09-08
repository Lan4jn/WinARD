# Apple MVS E2a 边界核实报告

**日期：** 2026-09-08  
**执行者：** 后续开发团队  
**核验目标：** E2a 载荷边界验证（大端长度消费与后继边界同步）  
**当前判定：** **E2a 边界假设首次由真实 Mac 实测闭环验证通过**

---

## 1. 实测运行记录与事实

在 2026-09-08 的真机采样会话中，执行完整采集命令：
```powershell
dotnet run --project tools/WinARD.ScaleProbe/WinARD.ScaleProbe.csproj -c Release -p:Platform=x64 -- `
  "$env:LOCALAPPDATA\WinARD\winard.db" <device-guid> `
  --capture-complete-mvs solid-blue artifacts/protocol-research/samples/solid-blue-complete --confirm-synthetic
```

### 终端实时输出（匿名脱敏）
```text
Initialized=6016x3384
Stage=baseline-100 PixelSize=6016x3384
Stage=baseline-100 FullPixelFrames=29 Seconds=6.119 FPS=4.74 MiBps=0.804
MvsSetupCaptured=True DeclaredLength=129 ActualLength=129 SHA256=C02CECE7B757BC6AF2C5CA3A74A4C0D798B375A27EEC3CC2DB9BD9FAF3B7FBAD
MvsSliceCaptured=True Rectangle=16x16 DeclaredLength=33 ActualLength=33 SHA256=4E90E93E4786D90A21B9A1764A22ADE42A1BEA235F9F347E1FC39D1789C8B72B
MvsCompleteCapture=Success Mode=Both SetupCaptured=True SliceCaptured=True SuccessorBoundaryValidated=True NextMessageType=0
SetupDetails DeclaredLength=129 ActualLength=129 SHA256=C02CECE7B757BC6AF2C5CA3A74A4C0D798B375A27EEC3CC2DB9BD9FAF3B7FBAD
SliceDetails Rectangle=16x16 DeclaredLength=33 ActualLength=33 Completeness=RecordCompleteUnderLengthHypothesis SHA256=4E90E93E4786D90A21B9A1764A22ADE42A1BEA235F9F347E1FC39D1789C8B72B
```

---

## 2. 样本落盘与十六进制证据

共完成 **3 次独立真机采样**（完全独立会话）：
1. `solid-blue-complete`（会话 1）
2. `solid-blue-complete-1`（会话 2）
3. `solid-blue-complete-2`（会话 3）

### (1) Setup 控制记录核验
- **文件：** 各目录下的 `setup/payload-prefix.bin`（133 字节，前 4 字节大端 129）
- **SHA-256：** 全部均为 `C02CECE7B757BC6AF2C5CA3A74A4C0D798B375A27EEC3CC2DB9BD9FAF3B7FBAD`
- **核验结论：** 3 次独立会话中 Setup 控制记录及其量化表字节 100% 稳定一致。

### (2) 图像切片记录核验与对比
各会话均在消费 $4 + N$ 字节后，验证后继消息类型严格为 `0`（`FramebufferUpdate`），`SuccessorBoundaryValidated = True`：

| 会话 | 目录 | 矩形 | declaredLength | 载荷 SHA-256 | 后继消息 |
|---|---|---|---:|---|---|
| 会话 1 | `solid-blue-complete/slice` | 16x16 at (0,0) | 33 | `4E90E93E4786D90A21B9A1764A22ADE42A1BEA235F9F347E1FC39D1789C8B72B` | 0 |
| 会话 2 | `solid-blue-complete-1/slice` | 16x16 at (0,0) | 14 | `BB48924E7C965836C86A0D4180BBF4D12C6FA32E9BEA23E81726E67209321561` | 0 |
| 会话 3 | `solid-blue-complete-2/slice` | 16x16 at (0,0) | 14 | `BB48924E7C965836C86A0D4180BBF4D12C6FA32E9BEA23E81726E67209321561` | 0 |

#### 十六进制切片头对比分析
- **会话 1 载荷 (33 字节)：**
  `00 0F 19 00 00 09 58 C3 68 1A 78 FD 11 8C 44 58 12 CC 64 07 62 31 3A 59 22 26 46 4F 2C 86 B6 B2 6D`
- **会话 2 载荷 (14 字节)：**
  `00 0F 19 00 00 09 59 36 80 1C B5 EA 2E DA`
- **会话 3 载荷 (14 字节)：**
  `00 0F 19 00 00 09 59 36 80 1C B5 EA 2E DA`

**极其关键的结构发现：**
所有切片载荷的前 6 个字节完全一致，固定为：
`00 0F 19 00 00 09`
- `00 0F`：即 15（16x16 块的行/列索引或最大坐标 $16-1=15$）；
- `19 00` / `00 09`：切片头标识、分量掩码或量化索引；
- 紧随其后的字节为自适应熵编码码流。

---

## 3. 验收结论：E2a 边界正式通关

对照 [Spec 规格文档](../superpowers/specs/2026-09-07-apple-mvs-handoff-spec.md) 第 4 节的验收标准：
1. [x] **区分三级记录状态**：`PrefixOnly`、`RecordCompleteUnderLengthHypothesis`、`SuccessorBoundaryValidated` 严格执行并写入 manifest；
2. [x] **配额安全守卫**：单记录上限 16 MiB、单批 64 MiB、超时门禁与安全防覆盖全部建立并自测通过；
3. [x] **按长度消费且后继头不失步**：所有样本消费 $N$ 字节后紧邻合法 `FramebufferUpdate` 消息头；
4. [x] **至少三个独立会话**：会话 1、会话 2、会话 3 连续三次真机独立采样全部成功对齐。

**结论：E2a（完整载荷边界与后继同步）已满足全部严苛通过条件，正式封板验收通过！**

---

## 4. 下一步工作（P3：图像内部语法与解码契约）

随着边界彻底确立，工作重心进入 **P3：图像内部语法实验与离线重建**：
1. 建立基于 `16x16` 宏块的离线解码分析实验；
2. 对比分析 `00 0F 19 00 00 09` 头部字段与后续压缩流（DCT 变换/熵编码/量化表逆变换）；
3. 编写语法解析器原型，验证能否还原出 16x16 像素色彩（如标准蓝色），达成 E2b 目标。
