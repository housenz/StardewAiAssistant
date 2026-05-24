# Stardew AI Assistant

注：项目由GPT5.5 VibeCoding 实现，建议搭配星露谷输入法修复MOD使用(链接在下方)

Stardew AI Assistant 是一个《星露谷物语》SMAPI Mod。它在游戏内提供 AI 问答面板，让玩家不用切出游戏，也能询问当前存档状态、NPC 日程、天气、运气、进度、物品资料等问题。

默认按 `L` 打开 AI 助手。

## 更新内容

- 优化了Agent编排，回答问题更精准
- 增加了wiki关键词本，提高了搜索精确度
- 解决重定向问题

## 功能特性

- 游戏内 AI 问答面板。
- 自动读取当前存档上下文，包括季节、日期、星期、时间、天气、明天天气、当前位置、金钱、体力、技能、背包、世界进度、NPC 好感度(目前 NPC 好感存在问题)等。
- 直接查询 biligame 星露谷 wiki：`https://wiki.biligame.com/stardewvalley/`。
- 内置本地 wiki 标题关键词本，查询前会先把问题匹配到真实页面标题，再访问在线 wiki，减少无效关键词搜索。
- 支持 wiki 重定向跟随，例如查询旧译名、别名或英文名时，会继续读取目标页面内容。
- wiki 页面会先清洗为文本全文再交给 AI。常见 NPC 页面通常可完整读取；特别长的页面会按上限截断并在日志中标记。
- 询问 NPC 位置时，会结合当前季节、天气、星期、时间、地图解锁状态和 wiki 日程进行判断。
- 支持 OpenAI-compatible API，例如 OpenAI、DeepSeek、OpenRouter、硅基流动等。
- 支持 Ollama 本地模型。
- 目前主要测试了 DeepSeek-V4 Flash 模型。
- 支持聊天历史、滚轮翻页、`PageUp` / `PageDown` 翻页、右上角按钮清空历史。

![picture](./picture/picture1_查看用户信息.png)

![picture](./picture/picture2_知识问答.png)

## 安装

前置需求：

- Stardew Valley
- SMAPI
- 星露谷输入中文存在问题，可以下载MOD:星露谷输入法修复 Input Method Fix：
    https://www.nexusmods.com/stardewvalley/mods/35313
    https://github.com/Cyrillya/InputMethodFix

把整个 `StardewAiAssistant` 压缩包解压到游戏的 `Mods` 目录。

如果仅使用MOD，请下载github右侧Releases压缩包，如果需要二次开发，请clone整个项目

Steam 常见路径：

```text
C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley\Mods\StardewAiAssistant
```

目录结构应类似：

```text
Mods
└─ StardewAiAssistant
   ├─ StardewAiAssistant.dll
   ├─ manifest.json
   ├─ config.json
   ├─ Data
   │  └─ wiki-titles.json
```

然后通过 SMAPI 启动游戏。

## 使用

1. 通过 SMAPI 启动游戏。
2. 进入存档。
3. 按 `L` 打开 AI 助手。
4. 输入问题后按 `Enter`。
5. 使用鼠标滚轮、`PageUp`、`PageDown` 查看历史记录。
6. 点击右上角 `清空` 清除当前会话历史。

## Agent 工作流程

Mod 会按多 Agent 编排处理问题，智能优先：

1. `supervisor-agent` 会先调用 AI，判断当前问题能否直接用游戏状态回答，还是必须查询 wiki。
2. `plan-agent` 会调用 AI 生成执行计划。需要 wiki 时，它会把自然语言问题拆成 biligame wiki 能搜索的短关键词或精确页面名。
3. 查询词会先经过本地标题关键词本匹配，例如 `枫糖浆 配方` 会回退到 `枫糖浆`，`猪车什么时候来` 会回退到 `旅行货车`。
4. `execute-agent` 按确定的页面标题调用 wiki 工具查询页面，并再次调用 AI 判断工具结果是否足够支持最终答案。
5. 如果结果不足，`plan-agent` 会执行 replan，再次调用 AI 根据失败日志生成新的关键词并重新查询。
6. `supervisor-agent` 最后再次调用 AI，汇总当前游戏状态、wiki 结果、执行日志和最近对话，输出最终答案。

这意味着一次复杂提问可能会触发多次 AI 请求，速度会比单次问答慢，但规划、检索和推理能力更强。(建议使用Flash模型，回复速度还是很快的)

如果当前状态和 wiki 查询结果都不足，AI 应明确说无法确定并说明缺少什么，而不是编造。

示例问题：

```text
现在海莉在哪里？
明天天气怎么样？
今天适合下矿吗？
夏天可以钓什么鱼？
我现在应该优先做什么？
```

## 配置

配置文件位于：

```text
Stardew Valley\Mods\StardewAiAssistant\config.json
```

修改配置后，建议重启游戏。

完整配置示例：

```json
{
  "OpenMenuButton": "L",
  "Provider": "OpenAICompatible",
  "ApiKey": "",
  "BaseUrl": "https://api.openai.com/v1",
  "Model": "gpt-4o-mini",
  "OllamaBaseUrl": "http://localhost:11434",
  "OllamaModel": "qwen2.5:7b",
  "DeepSeekThinking": "disabled",
  "DeepSeekReasoningEffort": "high",
  "Language": "zh-CN",
  "TimeoutMs": 12000,
  "MaxKnowledgeEntries": 6,
  "PreferLocalAnswer": true,
  "EnableDebugLogging": false
}
```

### OpenMenuButton

打开 AI 面板的按键。

```json
"OpenMenuButton": "L"
```

可以改为其他 SMAPI 支持的按键，例如 `K`、`F8` 等。

### Provider

选择 AI 服务类型。

```json
"Provider": "OpenAICompatible"
```

可选值：

- `OpenAICompatible`：使用 OpenAI-compatible 在线接口。
- `Ollama`：使用本机 Ollama 模型。

### ApiKey

在线 API 密钥。

```json
"ApiKey": "你的 API Key"
```

使用 `OpenAICompatible` 时通常必须填写。使用 `Ollama` 时通常留空。

### BaseUrl

在线 API 基础地址。

OpenAI 示例：

```json
"BaseUrl": "https://api.openai.com/v1"
```

DeepSeek 示例：

```json
"BaseUrl": "https://api.deepseek.com"
```

### Model

模型名。

OpenAI 示例：

```json
"Model": "gpt-4o-mini"
```

DeepSeek 示例：

```json
"Model": "deepseek-v4-flash"
```

### OllamaBaseUrl

Ollama 本地服务地址。

```json
"OllamaBaseUrl": "http://localhost:11434"
```

只有 `Provider` 为 `Ollama` 时使用。

### OllamaModel

Ollama 本地模型名。

```json
"OllamaModel": "qwen2.5:7b"
```

可以用下面命令查看已安装模型：

```powershell
ollama list
```

### DeepSeekThinking

DeepSeek thinking 开关。

```json
"DeepSeekThinking": "disabled"
```

游戏内快速问答建议保持 `disabled`。复杂推理可以改为 `enabled`，但速度会变慢。

### DeepSeekReasoningEffort

DeepSeek thinking 开启时的推理强度。

可选值：

```json
"DeepSeekReasoningEffort": "low"
```

```json
"DeepSeekReasoningEffort": "medium"
```

```json
"DeepSeekReasoningEffort": "high"
```

### Language

语言设置。

```json
"Language": "zh-CN"
```

当前建议保持默认。

### TimeoutMs

AI 请求超时时间，单位是毫秒。

```json
"TimeoutMs": 12000
```

如果网络较慢，可以调大，例如 `20000`。

### MaxKnowledgeEntries

每次最多使用多少条 wiki 查询结果。

```json
"MaxKnowledgeEntries": 6
```

数值越大，AI 可参考的信息越多，但响应可能更慢。

### PreferLocalAnswer

是否优先使用游戏内直接读取的信息回答。

```json
"PreferLocalAnswer": true
```

例如游戏当前能直接读取到 NPC 位置时，会优先直接回答。

### EnableDebugLogging

是否开启调试日志。

```json
"EnableDebugLogging": false
```

排查问题时可以改为 `true`。

开启后会在 Mod 文件夹生成：

```text
StardewAiAssistant-debug.txt
```

日志中重点看这些标记：

- `keyword-book-match`：本地 wiki 标题关键词本的命中情况。
- `wiki-query`：真正发起的 wiki 查询。
- `wiki-result`：每次 wiki 查询是否成功。
- `wiki-knowledge`：本轮汇总到的 wiki 页面文本，日志会显示内容长度和截断标记。
- `prompt-copy`：同一份 wiki 内容被复制进某个 Agent prompt，不代表重新查询。

注意：日志中的 `execute-review-prompt`、`final-supervisor-prompt` 可能会再次包含 wiki 内容，这是为了让不同 Agent 读取同一份资料；真实查询次数以 `wiki-query` 为准。

wiki 内容限制：

- 抓取阶段会保存清洗后的页面全文，最多约 `60000` 字。
- `execute-agent` 和最终 `supervisor-agent` 会按不同阶段截取部分内容放入 prompt，避免超过模型上下文。
- 如果发生截断，日志会出现类似 `...[truncated 60000/xxxxx chars]` 或 `...[truncated in log/prompt ...]` 的标记。

## DeepSeek 配置示例

```json
{
  "OpenMenuButton": "L",
  "Provider": "OpenAICompatible",
  "ApiKey": "你的 DeepSeek API Key",
  "BaseUrl": "https://api.deepseek.com",
  "Model": "deepseek-v4-flash",
  "OllamaBaseUrl": "http://localhost:11434",
  "OllamaModel": "qwen2.5:7b",
  "DeepSeekThinking": "disabled",
  "DeepSeekReasoningEffort": "high",
  "Language": "zh-CN",
  "TimeoutMs": 12000,
  "MaxKnowledgeEntries": 6,
  "PreferLocalAnswer": true,
  "EnableDebugLogging": false
}
```

## Ollama 配置示例

```json
{
  "OpenMenuButton": "L",
  "Provider": "Ollama",
  "ApiKey": "",
  "BaseUrl": "https://api.openai.com/v1",
  "Model": "gpt-4o-mini",
  "OllamaBaseUrl": "http://localhost:11434",
  "OllamaModel": "qwen2.5:7b",
  "DeepSeekThinking": "disabled",
  "DeepSeekReasoningEffort": "high",
  "Language": "zh-CN",
  "TimeoutMs": 20000,
  "MaxKnowledgeEntries": 6,
  "PreferLocalAnswer": true,
  "EnableDebugLogging": false
}
```

使用 Ollama 前，需要先启动 Ollama，并确保对应模型已经下载。

## AI 可读取的游戏信息

Mod 会尽量把以下信息交给 AI：

- 当前年、季节、日期、星期、时间
- 今天的天气、明天的天气
- 当前地图和坐标
- 玩家名、农场名
- 今日运气值
- 当前金钱、历史总收入
- 当前生命、体力
- 五项技能等级
- 背包物品摘要
- 钱包能力和部分解锁状态
- 矿洞层数、沙漠、下水道、温室、社区中心、姜岛等进度
- 星之果实数量和部分来源状态推断
- NPC 好感度摘要
- 当前任务列表

## 注意事项

- 本 Mod 需要网络访问 biligame wiki。离线时 wiki 查询会失败。
- NPC 日程判断依赖 wiki 页面内容和 AI 阅读能力，复杂节日、特殊事件、Mod 新增 NPC 可能不完全准确。
- 如果 AI 回答明显不准确，可以开启 `EnableDebugLogging`，然后查看 Mod 文件夹中的 `StardewAiAssistant-debug.txt`。
- API Key 不要公开分享。如果泄露，请立即撤销并重新生成。

## 开发构建

如果你需要自己编译：

```powershell
cd StardewAiAssistant
dotnet build -c Release
```

需要安装 .NET 6 SDK 或更高版本。

如果需要更新本地 wiki 标题关键词本：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\GenerateWikiTitles.ps1
```

脚本会从 biligame MediaWiki API 拉取主命名空间的非重定向页面标题，并生成：

```text
Data\wiki-titles.json
```

该文件会在构建时复制到 Mod 输出目录。运行时不会自动联网刷新关键词本。
