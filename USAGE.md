# N_m3u8DL-CLI Pro — 广告分片检测与移除 使用文档

## 1. 功能概述

N_m3u8DL-CLI Pro 在原版基础上新增了**广告分片检测与移除**功能。该功能在解析 m3u8 播放列表后、下载分片前，通过 5 种加权检测策略自动识别广告分片，将其从下载队列中移除，最终合并出不含广告的视频文件。

**适用场景：**

- 视频网站在片头或片中插入广告，m3u8 播放列表中包含广告分片
- 广告分片与正片分片在 URL 路径、域名、时长等方面存在可辨识差异
- HLS 流中包含 SCTE-35/CUE 标准广告标记

**不适用场景：**

- 广告与正片使用完全相同的 URL 模式和时长（无特征差异）
- 直播流（无固定分片列表）
- 广告嵌入正片画面内（非独立分片）

---

## 2. 快速开始

### 仅检测，不下载

```bash
N_m3u8DL-CLI.exe "https://example.com/video.m3u8" --adDetectOnly --adDetectReport report.json
```

### 下载并自动去广告

```bash
N_m3u8DL-CLI.exe "https://example.com/video.m3u8" --enableAdDetect
```

### 检测但不移除（仅标记）

```bash
N_m3u8DL-CLI.exe "https://example.com/video.m3u8" --enableAdDetect --adDetectNoRemove
```

---

## 3. 命令行参数

新增 6 个命令行参数：

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `--enableAdDetect` | 开关 | `false` | 启用广告分片检测与移除，下载时自动移除检测到的广告 |
| `--adDetectOnly` | 开关 | `false` | 仅检测广告并输出报告，不下载任何分片，检测完成后程序退出 |
| `--adDetectThreshold` | 数值 | `0.6` | 置信度阈值 (0.0-1.0)，低于此值的分片不会被判定为广告 |
| `--adDetectKeywords` | 字符串 | `""` | 自定义广告 URL 关键词，逗号分隔，追加到内置 14 个关键词之后 |
| `--adDetectReport` | 字符串 | `""` | 广告检测报告输出文件路径（JSON 格式） |
| `--adDetectNoRemove` | 开关 | `false` | 检测广告但不移除，用于预览哪些分片会被标记 |

**参数组合规则：**

- `--enableAdDetect` 和 `--adDetectOnly` 任一启用都会激活检测引擎
- `--adDetectOnly` 优先级更高：检测完成后程序直接退出，不进入下载流程
- `--adDetectNoRemove` 仅在 `--enableAdDetect` 模式下有效（与 `--adDetectOnly` 互斥无意义）
- `--adDetectThreshold`、`--adDetectKeywords`、`--adDetectReport` 在两种模式下均可使用

---

## 4. 检测原理

系统采用 **5 种独立策略 + 加权投票** 的检测机制。每个策略对每个分片独立评分（0.0-1.0，不参与则不计入），最终加权平均得到置信度，超过阈值则判定为广告。

### 策略总览

| 策略 | 权重 | 核心原理 | 最高评分 |
|------|------|----------|----------|
| CUE/SCTE-35 标记 | 0.30 | HLS 标准广告标记，最可靠的信号 | 0.9 |
| 域名/路径差异 | 0.25 | 广告分片通常来自不同 CDN 或 URL 路径 | 0.8 |
| 时长异常 | 0.20 | 广告分片时长常为固定值，与正片不同 | 0.7 |
| URL 关键词匹配 | 0.15 | URL 包含广告相关关键词 | 0.9 |
| 时长一致性 | 0.10 | 广告组内时长完全一致而正片有变化 | 0.5 |

### 策略 1：CUE/SCTE-35 标记检测（权重 0.30）

**原理：** HLS 协议定义了 SCTE-35 广告标记标准。当 m3u8 播放列表中出现 `#EXT-X-CUE-OUT` 时表示广告开始，`#EXT-X-CUE-IN` 表示广告结束。处于 CUE-OUT 区间内的分片即为广告。

**识别的标记：**

| 标记 | 含义 | 处理方式 |
|------|------|----------|
| `#EXT-X-CUE-OUT` | 广告开始 | 设置 `isInCueOut = true`，解析 DURATION 属性 |
| `#EXT-X-CUE-OUT-CONT` | 广告继续 | 维持 `isInCueOut = true` |
| `#EXT-X-CUE-IN` | 广告结束 | 设置 `isInCueOut = false` |
| `#EXT-X-CUE-SPAN` | 跨广告段 | 设置 `isInCueOut = true` |

**评分：** 分片处于 CUE-OUT 区间内 → **0.9**

**支持的 DURATION 格式：**
- `#EXT-X-CUE-OUT:DURATION=30`
- `#EXT-X-CUE-OUT:30`

### 策略 2：域名/路径差异检测（权重 0.25）

**原理：** 广告分片通常由专用广告 CDN 或独立路径提供，与正片分片的域名或路径前缀不同。

**检测逻辑：**
1. 按 `#EXT-X-DISCONTINUITY` 将分片分组
2. 找出总时长最大的组（主体内容组），提取其域名和公共路径前缀
3. 其他组中的分片若域名不同或路径前缀不匹配，则标记为可疑

**评分：**

| 情况 | 评分 | 示例 |
|------|------|------|
| 域名不同 | 0.8 | `ad-cdn.com` vs `video-cdn.com` |
| 路径不同 | 0.6 | `/video/adjump/...` vs `/video/movie/...` |

### 策略 3：时长异常检测（权重 0.20）

**原理：** 广告分片通常使用固定时长（如 3 秒），而正片分片时长可能有变化。该策略检测 DISCONTINUITY 组内时长完全一致且与主体内容时长差异明显的组。

**检测条件：**
- 组内分片时长完全一致（方差 < 0.001）
- 组总时长在 3-120 秒范围内（典型广告长度）

**评分：**

| 条件 | 评分 | 说明 |
|------|------|------|
| 时长差异 > 30% | 0.7 | 高度可疑，如广告 3s vs 正片 10s |
| 时长差异 10%-30% | 0.4 | 中度可疑 |

### 策略 4：URL 关键词匹配（权重 0.15）

**原理：** 广告分片的 URL 中常包含特定关键词（如 `adjump`、`preroll` 等）。

**内置 14 个关键词及评分：**

| 关键词 | 评分 | 说明 |
|--------|------|------|
| `adjump` | 0.9 | 国内视频站常见广告跳转路径 |
| `doubleclick` | 0.9 | Google 广告平台 |
| `googlesyndication` | 0.9 | Google 广告联盟 |
| `/ad/` | 0.85 | 明确的广告路径段 |
| `ad-` | 0.85 | 广告前缀 |
| `commercial` | 0.85 | 商业广告标识 |
| `preroll` | 0.85 | 片头广告 |
| `adserver` | 0.8 | 广告服务器 |
| `adserv` | 0.8 | 广告服务缩写 |
| `midroll` | 0.8 | 片中广告 |
| `postroll` | 0.8 | 片尾广告 |
| `_ad_` | 0.6 | 通用广告标识 |
| `advert` | 0.6 | 广告缩写 |
| `adplayer` | 0.6 | 广告播放器 |

> 自定义关键词通过 `--adDetectKeywords` 追加，不覆盖内置关键词。匹配为大小写不敏感的子串包含检查。

### 策略 5：时长一致性检测（权重 0.10）

**原理：** 广告分片组内时长通常完全一致（零方差），而正片分片组时长存在自然波动。

**检测条件：**
- 组内时长方差 < 0.001（高度一致）
- 组内至少 2 个分片
- 其他组存在非一致的时长（有方差）

**评分：** 满足条件 → **0.5**（低置信度，仅作辅助证据）

---

## 5. 置信度计算

### 计算公式

```
置信度 = Σ(策略评分 × 策略权重) / Σ(参与策略的权重)
判定结果 = 置信度 ≥ 阈值 → 广告
```

仅对给出评分（> 0）的策略参与计算，未给出评分的策略不纳入。

### 计算示例

以 btlshb.com 的片头广告分片为例：

| 策略 | 评分 | 权重 | 加权值 |
|------|------|------|--------|
| DomainPath | 0.6 | 0.25 | 0.15 |
| URLKeyword | 0.9 | 0.15 | 0.135 |

```
置信度 = (0.15 + 0.135) / (0.25 + 0.15) = 0.285 / 0.40 = 0.7125
判定: 0.7125 ≥ 0.6 → 广告 ✓
```

### 阈值调节建议

| 阈值 | 效果 | 适用场景 |
|------|------|----------|
| 0.4-0.5 | 宽松，更多分片被标记为广告 | 广告特征明显但漏检较多时 |
| **0.6（默认）** | **平衡** | **大多数场景** |
| 0.7-0.8 | 严格，仅高置信度分片被标记 | 正片被误判为广告时 |

**调节原则：** 先用 `--adDetectOnly` 预览，根据报告调整阈值后再正式下载。

---

## 6. 使用场景与示例

### 场景 1：预览检测结果

在不下载的情况下查看哪些分片会被识别为广告：

```bash
N_m3u8DL-CLI.exe "https://example.com/video.m3u8" ^
    --adDetectOnly ^
    --adDetectReport D:\reports\ad_report.json
```

输出：控制台打印检测报告 + JSON 文件保存至指定路径，程序随后退出。

### 场景 2：下载并自动移除广告

最常用的模式，下载时自动跳过广告分片：

```bash
N_m3u8DL-CLI.exe "https://example.com/video.m3u8" ^
    --enableAdDetect ^
    --workDir D:\downloads ^
    --saveName "电影名"
```

下载完成后合并的 MP4 文件中不包含广告。

### 场景 3：自定义关键词和阈值

针对特定网站的广告 URL 模式添加关键词：

```bash
N_m3u8DL-CLI.exe "https://example.com/video.m3u8" ^
    --enableAdDetect ^
    --adDetectKeywords "adjump,ad/preroll,banner" ^
    --adDetectThreshold 0.5
```

降低阈值至 0.5 使检测更宽松，同时追加 3 个自定义关键词。

### 场景 4：检测但保留广告分片

想确认检测效果但暂时保留所有分片：

```bash
N_m3u8DL-CLI.exe "https://example.com/video.m3u8" ^
    --enableAdDetect ^
    --adDetectNoRemove ^
    --adDetectReport D:\reports\preview.json
```

下载完整视频，控制台仍会显示广告检测结果，但不会移除任何分片。

### 场景 5：结合 ffmpeg 合并为 MP4

确保 ffmpeg.exe 位于当前目录、程序目录或系统 PATH 中：

```bash
N_m3u8DL-CLI.exe "https://example.com/video.m3u8" ^
    --enableAdDetect ^
    --enableMuxFastStart ^
    --workDir D:\downloads ^
    --saveName "电影名"
```

> `--enableMuxFastStart` 使 MP4 支持边下边播（moov atom 前置）。

### 场景 6：指定 ffmpeg 路径

程序按以下顺序查找 ffmpeg.exe：
1. 当前工作目录
2. 程序 exe 所在目录
3. 系统 PATH 环境变量

若 ffmpeg 不在以上位置，可将其复制到程序同目录：

```bash
copy "C:\path\to\ffmpeg.exe" "D:\downloads\ffmpeg.exe"
cd /d D:\downloads
N_m3u8DL-CLI.exe "https://example.com/video.m3u8" --enableAdDetect
```

---

## 7. 输出格式

### 控制台报告

```
====== 广告检测报告 ======
总分片数: 1746
检测到广告分片: 18
广告总时长: 52.0s / 6965.9s
置信度阈值: 0.60

--- 广告分片详情 ---
  分片 #75 [时长:3.0s] [置信度:0.71]
    DomainPath: 0.60 (路径不同: /video/adjump/time/17766952429730000000....)
    URLKeyword: 0.90 (URL包含关键词: adjump)
  分片 #76 [时长:3.0s] [置信度:0.71]
    DomainPath: 0.60 (路径不同: /video/adjump/time/17766952429740000001....)
    URLKeyword: 0.90 (URL包含关键词: adjump)
  ...

========================
```

### JSON 报告

通过 `--adDetectReport` 参数指定输出路径，格式如下：

```json
{
  "totalSegments": 1746,
  "adSegmentCount": 18,
  "totalAdDuration": 52.0,
  "totalDuration": 6965.95,
  "threshold": 0.6,
  "adSegments": [
    {
      "index": 75,
      "confidence": 0.7125,
      "duration": 3.0,
      "url": "https://p.bvvvvvvvvv1f.com/video/adjump/time/17766952429730000000.ts",
      "groupIndex": 1,
      "strategyScores": {
        "DomainPath": 0.6,
        "URLKeyword": 0.9
      },
      "strategyReasons": {
        "DomainPath": "路径不同: /video/adjump/time/17766952429730000000....",
        "URLKeyword": "URL包含关键词: adjump"
      }
    }
  ]
}
```

**字段说明：**

| 字段 | 类型 | 说明 |
|------|------|------|
| `totalSegments` | int | 参与分析的分片总数 |
| `adSegmentCount` | int | 检测到的广告分片数 |
| `totalAdDuration` | float | 广告分片总时长（秒） |
| `totalDuration` | float | 所有分片总时长（秒） |
| `threshold` | float | 使用的置信度阈值 |
| `adSegments` | array | 被标记为广告的分片列表 |
| `adSegments[].index` | long | 分片在 m3u8 中的序号 |
| `adSegments[].confidence` | float | 加权置信度 |
| `adSegments[].duration` | float | 分片时长（秒） |
| `adSegments[].url` | string | 分片下载 URL |
| `adSegments[].groupIndex` | int | DISCONTINUITY 分组索引 |
| `adSegments[].strategyScores` | object | 各策略评分 |
| `adSegments[].strategyReasons` | object | 各策略判定原因 |

---

## 8. 实际验证案例

### btlshb.com 验证（谍影重重3）

**测试命令：**

```bash
N_m3u8DL-CLI.exe "https://p.bvvvvvvvvv1f.com/video/dieyingzhongzhong3/HD%E4%B8%AD%E5%AD%97/index.m3u8" ^
    --enableAdDetect ^
    --workDir D:\down\m3u8dl ^
    --saveName "谍影重重3_无广告" ^
    --enableMuxFastStart
```

**检测结果：**

| 指标 | 数值 |
|------|------|
| 总分片数 | 1746 |
| 检测到广告分片 | 18 |
| 广告总时长 | 52.0s |
| 视频总时长 | 6965.9s (约 1h56m) |

**广告分布：**

| 位置 | 分片范围 | 分片数 | 时长 | 命中策略 |
|------|----------|--------|------|----------|
| 片头广告 | #75 - #83 | 9 | 26s (8×3s + 1×2s) | DomainPath(0.6) + URLKeyword(0.9) |
| 片中广告 | #609 - #617 | 9 | 26s (8×3s + 1×2s) | DomainPath(0.6) + URLKeyword(0.9) |

**命中原因：** 广告分片 URL 路径为 `/video/adjump/time/...`，与正片路径不同且包含关键词 `adjump`，加权置信度 0.7125。

**输出文件：** `D:\down\m3u8dl\谍影重重3_无广告.mp4`，1.22GB，01:57:17，H.264 1920×800 + AAC。

---

## 9. 常见问题

### Q: 正片被误判为广告怎么办？

调高阈值至 0.7 或 0.8：

```bash
--adDetectThreshold 0.7
```

先用 `--adDetectOnly` 预览报告，确认无误判后再下载。

### Q: 广告没有被检测到怎么办？

1. 用 `--adDetectOnly` 查看报告，确认哪些策略未命中
2. 如果广告 URL 有明显特征关键词，添加自定义关键词：

```bash
--adDetectKeywords "custom_ad_keyword,another_keyword"
```

3. 降低阈值至 0.5 使检测更宽松：

```bash
--adDetectThreshold 0.5
```

### Q: 为什么 DurationAnomaly 和 DurationConsistency 没有命中？

这两个策略要求 m3u8 中存在 `#EXT-X-DISCONTINUITY` 标记将广告分片与正片分片分组。如果广告和正片在同一组内（无 DISCONTINUITY 分隔），则组内统计策略无法生效。

### Q: ffmpeg 找不到怎么办？

程序按以下顺序查找 ffmpeg.exe：
1. 当前工作目录
2. N_m3u8DL-CLI.exe 所在目录
3. 系统 PATH 环境变量

将 ffmpeg.exe 复制到以上任一位置即可。若合并失败可手动合并：

```bash
cd "D:\downloads\视频目录"
ffmpeg.exe -f concat -safe 0 -i merge_list.txt -c copy -movflags +faststart -bsf:a aac_adtstoasc output.mp4
```

其中 `merge_list.txt` 需自行生成，每行格式为 `file 'Part_X/XXXX.ts'`。

### Q: 移除广告后视频播放异常（花屏、卡顿）？

广告分片与正片分片通过 `#EXT-X-DISCONTINUITY` 分隔，说明它们的编码参数可能不同（分辨率、帧率、编码器等）。移除广告后，DISCONTINUITY 边界两侧的分片被直接拼接，如果编码不兼容可能导致播放问题。

解决方案：用 ffmpeg 重新编码而非 copy 模式合并：

```bash
ffmpeg -f concat -safe 0 -i merge_list.txt -c:v libx264 -c:a aac output.mp4
```

### Q: 加密分片移除广告后解密失败？

广告分片和正片分片可能使用不同的 `#EXT-X-KEY` 加密密钥。移除广告后，如果相邻分片的密钥不同，解密会失败。目前程序不会修改加密信息，依赖原有的 KEY 解析逻辑。遇到此问题时建议使用 `--adDetectNoRemove` 保留所有分片。

---

## 10. 注意事项与局限性

1. **依赖特征差异**：本功能基于广告与正片在 URL、时长、标记等方面的特征差异。如果广告分片与正片完全一致（相同域名、相同路径模式、相同时长、无 CUE 标记），则无法检测。

2. **需要 DISCONTINUITY 分组**：域名路径差异、时长异常、时长一致性这 3 个策略依赖 `#EXT-X-DISCONTINUITY` 标记进行分组。如果 m3u8 中广告分片与正片分片未被 DISCONTINUITY 分隔，这些策略不会生效。但 CUE 标记和 URL 关键词策略不受此限制。

3. **不支持直播流**：广告检测仅适用于点播 (VOD) m3u8，直播流没有固定的分片列表。

4. **阈值需根据网站调整**：不同网站的广告特征强度不同，默认阈值 0.6 适用于大多数场景，但可能需要根据实际情况微调。

5. **DISCONTINUITY 边界处理**：移除广告后，如果广告分片前后都有 DISCONTINUITY 标记，程序会移除空分组，但不会合并相邻的 DISCONTINUITY 分组。在极少数情况下可能导致视频播放时出现短暂卡顿。

6. **与 Youku 去广告共存**：程序原有的 Youku 广告移除逻辑 (`DelAd`) 会先执行，之后广告检测引擎再执行。两者互不干扰。

7. **`--adDetectOnly` 会中断程序**：该模式通过 `Environment.Exit(0)` 退出程序，不会执行后续的下载和合并操作。
