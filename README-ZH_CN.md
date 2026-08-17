# ModelMux

ModelMux 是一个「模型多路复用器」——轻量级、能力感知（capability-aware）的 LLM 网关，根据请求所需能力（文本 / 图片 / 音频）将每个请求“复用”到合适的模型，并支持自动降级（fallback）、模型名重写与多轮多模态剥离。

使用 .NET 10 AOT 编译为单一原生二进制（约 12 MB），无 .NET 运行时依赖。

## 功能特性

- **统一 API 入口**：客户端使用单一 API Key 访问，modelmux 根据请求中的模型名自动路由到对应上游
- **Fallback 链**：主模型不可用时自动降级到备用模型，连接失败和上游 4xx/5xx 均触发 fallback
- **能力路由**：模型可声明 `type`（能力集合），当请求携带 image/audio 内容而主模型不支持时，自动沿 fallback 链查找支持该能力的模型并转发
- **多轮对话剥离**：转发前自动剥离历史消息中的 image/audio 内容块（保留文字），避免纯文本模型因历史图片报错
- **模型名映射**：客户端请求的模型名自动替换为上游实际模型名（config key 即上游模型名）
- **透明代理**：支持所有 OpenAI 兼容 API（`/v1/chat/completions`、`/v1/embeddings`、`/v1/images/*`、`/v1/audio/*` 等）
- **流式 SSE**：完整支持 `stream: true` 场景
- **默认参数注入**：可为每个模型配置默认参数（支持 number/string/bool/object），客户端未传时自动注入
- **健康检查**：后台轮询上游 `/v1/models` + 请求失败即时标记，30 秒 cooldown 自动重试
- **热更新**：`config.json` 修改后自动重载，无需重启
- **AOT 编译**：PublishAot 编译为单一原生二进制（约 12 MB），无 .NET 运行时依赖
- **Systemd 集成**：日志输出 stdout，部署为 systemd 服务后自动进入 journal

## 前置条件

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)（仅编译时需要，运行时不依赖）
- Linux x64 运行环境（glibc 2.23+ / musl 均可）

## 快速开始

### 1. 克隆项目

```bash
git clone https://github.com/gornear/modelmux.git
cd modelmux
```

### 2. 创建配置文件

首次部署时，运行以下命令生成脱敏的示例配置：

```bash
modelmux generateconfig
```

该命令会在当前目录生成 `config.json.example`（若目录下已有 `config.json`，则从它脱敏生成，保留结构但将 `apiKey` 替换为 `your-api-key`；否则使用内置模板）。

然后复制为真实配置并编辑：

```bash
cp config.json.example config.json
```

编辑 `config.json`，配置您的上游模型：

```json
{
  "localvllm": [
    {
      "baseUrl": "http://192.168.1.100:8000/v1",
      "apiKey": "your-local-api-key",
      "models": [
        {
          "modelid": "gemma4-it-31b",
          "type": ["text", "image"],
          "fallback": [
            "deepseek/deepseek-v4-flash",
            "deepseek/deepseek-v4-pro"
          ],
          "defaultParams": {
            "temperature": 0.0,
            "top_p": 0.9,
            "top_k": 40
          }
        },
        {
          "modelid": "gemma4-it-31b",
          "type": ["text", "image"],
          "alias": "gemma4-it-31b-thinking",
          "fallback": [
            "deepseek/deepseek-v4-flash-thinking",
            "deepseek/deepseek-v4-pro-thinking"
          ],
          "defaultParams": {
            "temperature": 0.0,
            "top_p": 0.9,
            "top_k": 40,
            "chat_template_kwargs": {"enable_thinking": true}
          }
        }
      ]
    }
  ],
  "deepseek": [
    {
      "baseUrl": "https://api.deepseek.com",
      "apiKey": "your-model-api-key",
      "models": [
        {
          "modelid": "deepseek-v4-pro",
          "type": ["text"],
          "fallback": [
            "localvllm/gemma4-it-31b-thinking"
          ],
          "defaultParams": {
            "temperature": 0.0,
            "top_p": 0.9,
            "top_k": 40,
            "thinking": {
              "type": "disabled"
            }
          }
        },
        {
          "modelid": "deepseek-v4-flash",
          "type": ["text"],
          "fallback": [
            "localvllm/gemma4-it-31b"
          ],
          "defaultParams": {
            "temperature": 0.0,
            "top_p": 0.9,
            "top_k": 40,
            "thinking": {
              "type": "disabled"
            }
          }
        },
        {
          "modelid": "deepseek-v4-pro",
          "type": ["text"],
          "alias": "deepseek-v4-pro-thinking",
          "defaultParams": {
            "temperature": 0.0,
            "top_p": 0.9,
            "top_k": 40
          }
        },
        {
          "modelid": "deepseek-v4-flash",
          "type": ["text"],
          "alias": "deepseek-v4-flash-thinking",
          "defaultParams": {
            "temperature": 0.0,
            "top_p": 0.9,
            "top_k": 40
          }
        }
      ]
    }
  ]
}
```

编辑 `appsettings.json`，设置监听地址和统一 API Key。

> **提示**：`appsettings.json` 在首次 `modelmux serve` 启动时若不存在会自动生成（`ApiKey` 填充占位符 `sk-modelmux-local-key-change-me`），需编辑设为您的真实密钥。

```json
{
    "Kestrel": {
        "Endpoints": {
            "Http": {
                "Url": "http://0.0.0.0:5000"
            }
        },
        "Limits": {
            "MaxConcurrentConnections": 200,
            "MaxConcurrentUpgradedConnections": 200,
            "KeepAliveTimeout": "00:02:00"
        }
    },
    "ApiKey": "",
    "RequestTimeoutSeconds": 300,
    "MaxRetryAttempts": 3,
    "DebugPrompt": false,
    "HealthCheck": {
        "Enabled": true,
        "IntervalSeconds": 30,
        "TimeoutSeconds": 5,
        "UnhealthyCooldownSeconds": 30
    },
    "Logging": {
        "LogLevel": {
            "Default": "Information",
            "System.Net.Http.HttpClient": "Warning"
        }
    }
}
```

> **提示**：`baseUrl` 可以写 `http://host:port` 也可以写 `http://host:port/v1`。
>
> **模型别名（alias）**：使用 `alias` 可以让同一个上游模型以不同名称对外暴露，并搭配不同的 `defaultParams`。例如同一个 vLLM 模型可同时以 `local/gemma4-it-31b` 和 `local/gemma4-it-31b-thinking` 列出——上游在两种情况下收到的 `"model"` 都是 `"gemma4-it-31b"`，但 thinking 版本会注入 `"chat_template_kwargs": {"enable_thinking": true}`。
>
> **能力路由（type）**：使用 `type` 字段声明模型支持的能力集合。当客户端请求携带 image/audio 内容（例如 `messages[].content` 中的 `image_url`/`audio_url` 块，或访问 `/v1/images/*`、`/v1/audio/*` 路径），而主模型不支持对应能力时，modelmux 会自动沿 `fallback` 链查找支持该能力的模型并转发；若整条链都无匹配，则返回 `capability_unavailable` 错误。纯文本请求不受影响（所有模型默认支持 text）。
>
> **能力判定只看最后一条消息**：多轮对话时，历史消息中的 image/audio 已在前序轮次被模型识别并转化为文字回复，不再需要重新“看图”。因此能力判定只针对**当前这一轮（messages 数组最后一条）**携带的内容与请求路径，历史消息中的 image/audio 不参与能力判定。
>
> **转发前自动剥离历史多模态内容**：无论目标模型能力如何，modelmux 在转发时都会剥离除最后一条消息外的所有历史消息中的 `image_url`/`audio_url` 块（保留 `text` 块），避免纯文本上游（如 deepseek）收到历史图片报 400。
>
> **示例**：`deepseek/deepseek-v4-flash` 是纯文本模型（`"type": ["text"]`），若它的 `fallback` 里配置了一个多模态模型（`"type": ["text","image"]`），则客户端给 flash 发送图片时会自动切换到那个多模态模型，而不是把图片硬塞给文本模型。

### 3. 编译

```bash
dotnet publish -c Release -o publish
```

产物在 `publish/modelmux`（单一二进制，约 12 MB，不含任何配置文件）。

> 也可用 `dotnet run` 直接开发调试。

### 4. 运行

命令格式：

```bash
./modelmux                  # 打印帮助与部署指引
./modelmux serve            # 启动网关（首次自动生成 appsettings.json）
./modelmux generateconfig   # 当前目录生成 config.json.example
./modelmux --help | -h      # 打印帮助
./modelmux --version        # 打印版本号
```

启动网关：

```bash
./modelmux serve
```

输出：

```
info: ModelMux starting on http://0.0.0.0:5000
info: Health check service started (interval: 30s, timeout: 5s)
```

### 5. 测试

```bash
# 健康检查（无需认证）
curl http://localhost:5000/health

# 列出可用模型
curl -H "Authorization: Bearer sk-your-unified-api-key" http://localhost:5000/v1/models

# Chat Completions
curl -H "Authorization: Bearer sk-your-unified-api-key" \
     -H "Content-Type: application/json" \
     -d '{"model":"local/gemma4","messages":[{"role":"user","content":"Hello!"}]}' \
     http://localhost:5000/v1/chat/completions

# 流式
curl -N -H "Authorization: Bearer sk-your-unified-api-key" \
     -H "Content-Type: application/json" \
     -d '{"model":"deepseek/deepseek-v4-flash","messages":[{"role":"user","content":"Hello!"}],"stream":true}' \
     http://localhost:5000/v1/chat/completions
```

## 工作原理

### 基础路由链

```
客户端  ──POST /v1/chat/completions──→  ModelMux  ──POST /v1/chat/completions──→  上游 A
  "model":"local/gemma4"                │  替换 "model":"gemma4"                        (vllm)
                                        │  注入默认参数
                                        │
                                    上游 A 不可达？
                                        │
                                        └──POST /v1/chat/completions──→  上游 B
                                            "model":"deepseek-v4-flash"    (deepseek)
```

### 能力路由（图片 / 音频）

modelmux 会根据**当前这一轮的请求内容**判断是否需要 image/audio 能力，并在 fallback 链中查找匹配的模型：

```
收到请求，解析出 model 与 request body
        │
        ▼
① 判定所需能力（只看最后一条消息 + 请求路径）
   ├─ 路径为 /v1/images/*   → 需要 IMAGE
   ├─ 路径为 /v1/audio/*    → 需要 AUDIO
   ├─ 最后一条消息含 image_url/input_image → 需要 IMAGE
   └─ 最后一条消息含 audio_url/input_audio → 需要 AUDIO
        │
        ▼
② 沿 fallback 链查找（能力 + 健康双重过滤）
   ├─ 主模型支持所需能力且健康 → 用它
   ├─ 不支持/不可用 → 继续沿 fallback 链（含嵌套）递归查找
   └─ 整条链都无匹配 → 返回 capability_unavailable 错误
        │
        ▼
③ 转发前剥离历史多模态内容（见下）
```

### 多轮对话中的图片/音频剥离

多轮对话时，客户端（如 hermes-agent）会把**完整的对话历史**随每次请求一起发送。
前几轮的图片/音频已被模型识别并转化为文字回复（存于历史中的 assistant 消息），
无需再次发送。modelmux 在转发前会：

- **剥离除最后一条消息外**，所有历史消息 `content` 数组中的
  `image_url` / `input_image` / `audio_url` / `input_audio` 块
- **保留 text 块**（用户当轮的文字提问不丢）
- **保留最后一条消息原样**（当前轮的新图片/音频需正常处理）

这样纯文本模型（如 deepseek）在后续纯文本轮次不会因历史中的 image_url 而拒绝请求（400），
而图片轮次仍会正确路由到多模态模型并保留新图片。

```
第1轮（文本）  →  deepseek-v4-flash          ✓ 纯文本 → 文本模型
第2轮（图片）  →  local/current               ✓ 图片 → 多模态模型（本地）
第3轮（文本）  →  deepseek-v4-flash          ✓ 历史图片被剥离，纯文本 → 回文本模型
   └─ 转发时：前两轮的 image_url 块被删除，只保留文字提问与回复
```


## API 规范

### 认证

除 `/health` 外所有端点需携带统一 API Key：

```
Authorization: Bearer sk-your-unified-api-key
```

### 端点

| 端点 | 方法 | 认证 | 说明 |
|------|------|------|------|
| `/health` | GET | 无 | 服务存活检测 |
| `/v1/models` | GET | Bearer | 返回 config.json 中配置的模型列表 |
| `/v1/chat/completions` | POST | Bearer | Chat Completions（支持 stream） |
| `/v1/{**path}` | ANY | Bearer | 透明代理到上游（embeddings/images/audio 等） |

### 错误响应

```json
{
  "error": {
    "message": "No healthy routes found for model 'unknown-model'",
    "type": "no_route"
  }
}
```

能力不满足时返回 `capability_unavailable`：

```json
{
  "error": {
    "message": "No healthy model supporting 'IMAGE' found for model 'deepseek/deepseek-v4-flash'",
    "type": "capability_unavailable"
  }
}
```

## 配置参考

### appsettings.json

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `Kestrel.Endpoints.Http.Url` | string | `http://0.0.0.0:5000` | 监听地址 |
| `Kestrel.Limits.MaxConcurrentConnections` | int | 200 | 最大并发连接 |
| `ApiKey` | string | — | 统一 API Key（客户端使用） |
| `RequestTimeoutSeconds` | int | 300 | 上游请求超时（秒） |
| `MaxRetryAttempts` | int | 3 | Fallback 最大尝试次数 |
| `DebugPrompt` | bool | false | 设为 true 时，Debug 级别日志输出 messages 内容（注意隐私） |
| `HealthCheck.Enabled` | bool | true | 是否启用后台健康检查 |
| `HealthCheck.IntervalSeconds` | int | 30 | 轮询间隔（秒） |
| `HealthCheck.TimeoutSeconds` | int | 5 | 健康检查请求超时（秒） |
| `HealthCheck.UnhealthyCooldownSeconds` | int | 30 | 标记不健康后的冷却时间（秒），过期后允许重试 |
| `Logging.LogLevel.Default` | string | `Information` | 日志级别 |

### config.json

| 参数 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `<provider>` | string | 是 | Provider 命名空间（如 `local`、`deepseek`、`openai`） |
| `<provider>[].baseUrl` | string | 是 | 上游 API 地址（如 `https://api.deepseek.com`，含/不含 `/v1` 均可） |
| `<provider>[].apiKey` | string | 是 | 上游 API Key（端点组内所有模型共享） |
| `<provider>[].models` | array | 是 | 该端点组下的模型列表 |
| `models[].modelid` | string | 是 | 上游模型名，实际发送给 Provider API 的名称 |
| `models[].alias` | string | 否 | 对外暴露的别名（在 `/v1/models` 中显示为 `provider/alias`）。设置后客户端使用别名访问，但上游收到的仍然是 `modelid`。适用于为同一模型暴露思考/非思考等不同参数变体。 |
| `models[].type` | string[] | 否 | 模型支持的能力集合（如 `["text"]`、`["text","image"]`、`["text","image","audio"]`）。缺省时默认为 `["text"]`。用于能力路由：当请求需要 image/audio 能力而当前模型不支持时，自动沿 fallback 链查找支持该能力的模型。 |
| `models[].defaultParams` | object | 否 | 默认参数（支持 number/string/bool/object），客户端未传时注入 |
| `models[].fallback` | string[] | 否 | 备用模型列表，使用 `provider/modelid` 格式引用 |

### 常见配置示例

**DeepSeek**：
```json
"deepseek": [
  {
    "baseUrl": "https://api.deepseek.com",
    "apiKey": "sk-xxx",
    "models": [
      { "modelid": "deepseek-chat" }
    ]
  }
]
```

**OpenAI**：
```json
"openai": [
  {
    "baseUrl": "https://api.openai.com",
    "apiKey": "sk-xxx",
    "models": [
      {
        "modelid": "gpt-4o",
        "defaultParams": { "temperature": 0.7 }
      }
    ]
  }
]
```

**本地 vLLM**：
```json
"local": [
  {
    "baseUrl": "http://192.168.1.100:8000/v1",
    "apiKey": "not-needed",
    "models": [
      {
        "modelid": "qwen3",
        "fallback": ["deepseek/deepseek-chat"]
      }
    ]
  }
]
```

**模型别名（思考/非思考变体）**：
```json
"local": [
  {
    "baseUrl": "http://192.168.1.100:8000/v1",
    "apiKey": "not-needed",
    "models": [
      {
        "modelid": "qwen3-235b",
        "defaultParams": { "temperature": 0.0 }
      },
      {
        "modelid": "qwen3-235b",
        "alias": "qwen3-235b-thinking",
        "defaultParams": { "temperature": 0.0, "enable_thinking": true }
      }
    ]
  }
]
```
> **alias 原理**：别名成为对外暴露的名称（`local/qwen3-235b-thinking`），但上游 API 实际收到 `"model":"qwen3-235b"`。区别在于请求体中会注入 `enable_thinking: true`。

## 部署

### Systemd（推荐）

```bash
sudo cp publish/modelmux /opt/modelmux/
sudo chmod +x /opt/modelmux/modelmux

# 首次启动会自动生成 appsettings.json；或用 generateconfig 生成 config.json.example
cd /opt/modelmux
sudo ./modelmux generateconfig
sudo cp config.json.example config.json
# 编辑 config.json 与 appsettings.json 填入真实配置
```

创建 `/etc/systemd/system/modelmux.service`：

```ini
[Unit]
Description=ModelMux
After=network.target

[Service]
Type=simple
WorkingDirectory=/opt/modelmux
ExecStart=/opt/modelmux/modelmux serve
Restart=always
RestartSec=5
User=nobody
Group=nogroup
Environment=DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now modelmux
sudo journalctl -u modelmux -f   # 查看日志
```

## License

MIT
