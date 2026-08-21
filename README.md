# ModelMux

[中文说明](README-ZH_CN.md)

ModelMux is a model multiplexer — a lightweight, capability-aware gateway that muxes every request to the right model based on its required capabilities (text / image / audio), with automatic fallback, model-name rewriting, and multi-turn multimodal stripping.

Compiled via .NET 10 AOT into a single native binary (~12 MB) with no .NET runtime required.

## Features

- **Unified API Entry** — clients use a single API key; ModelMux dispatches to the correct upstream based on the model name in each request
- **Fallback Chain** — automatically degrades to backup models when the primary is unreachable or returns 4xx/5xx
- **Capability Routing** — models declare a `type` (capability set); when a request carries image/audio content the primary model doesn't support, ModelMux automatically finds a capable model along the fallback chain
- **Multi-turn Multimodal Stripping** — strips image/audio content blocks from historical messages (keeps text) before forwarding, so text-only upstreams don't reject requests due to stale images
- **Model Name Rewriting** — the client's requested model name is transparently replaced with the upstream model name (the `config.json` key *is* the upstream model name)
- **Transparent Proxy** — forwards all OpenAI-compatible endpoints (`/v1/chat/completions`, `/v1/embeddings`, `/v1/images/*`, `/v1/audio/*`, etc.)
- **SSE Streaming** — full support for `stream: true` Server-Sent Events
- **Default Parameter Injection** — configure default body parameters per model (supports number/string/bool/object). Client-supplied values are never overwritten
- **Health Checking** — background polling of upstream `/v1/models` + immediate marking on request failure; 30-second cooldown before automatic retry
- **Hot Reload** — `config.json` changes are picked up automatically without restart
- **AOT Compiled** — single native binary, no .NET runtime required
- **Systemd Ready** — logs to stdout, integrates with `journalctl`

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (build only, not required at runtime)
- Linux x86-64 (glibc 2.23+ or musl)

## Quick Start

### 1. Clone

```bash
git clone https://github.com/gornear/modelmux.git
cd modelmux
```

### 2. Configure

Generate a redacted example config:

```bash
modelmux generateconfig
```

This creates `config.json.example` in the current directory (redacted from an existing `config.json` if present, otherwise from the built-in template). Copy it and edit:

```bash
cp config.json.example config.json
```

Edit `config.json` to add your upstream models:

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

Edit `appsettings.json` to set the listen address and unified API key:

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

> **Note**: `baseUrl` may include a path prefix (e.g. `http://host:8000/v1`) or not. The program automatically deduplicates overlapping path prefixes — no `/v1/v1` double-writes.
>
> **Model Alias**: Use `alias` to expose the same upstream model under one or more public names with different `defaultParams`. It accepts a single string or an array of strings. For example, the same vLLM model can be listed as both `local/gemma4-it-31b` and `local/gemma4-it-31b-thinking` — the upstream receives `"model":"gemma4-it-31b"` in both cases, but the thinking variant injects `"chat_template_kwargs": {"enable_thinking": true}`. With an array you could expose it under `["gemma4-it-31b", "gemma4-it-31b-thinking"]` in one entry.

### 3. Build

```bash
dotnet publish -c Release -o publish
```

Output: `publish/modelmux` ~12 MB single binary (no config files bundled).

For development, use `dotnet run`.

### 4. Run

Command syntax:

```bash
./modelmux                  # Print help and deployment guide
./modelmux serve            # Start the gateway (auto-generates appsettings.json on first start)
./modelmux generateconfig   # Generate config.json.example in the current directory
./modelmux --help | -h      # Print help
./modelmux --version        # Print version
```

Start the gateway:

```bash
./modelmux serve
```

Output:
```
info: ModelMux starting on http://0.0.0.0:5000
info: Health check service started (interval: 30s, timeout: 5s)
```

### 5. Test

```bash
# Health check (no auth)
curl http://localhost:5000/health

# List models
curl -H "Authorization: Bearer sk-your-unified-api-key" http://localhost:5000/v1/models

# Chat Completions
curl -H "Authorization: Bearer sk-your-unified-api-key" \
     -H "Content-Type: application/json" \
     -d '{"model":"local/gemma4","messages":[{"role":"user","content":"Hello!"}]}' \
     http://localhost:5000/v1/chat/completions

# Streaming
curl -N -H "Authorization: Bearer sk-your-unified-api-key" \
     -H "Content-Type: application/json" \
     -d '{"model":"deepseek/deepseek-v4-flash","messages":[{"role":"user","content":"Hello!"}],"stream":true}' \
     http://localhost:5000/v1/chat/completions
```

## How It Works

### Basic Routing Chain

```
Client  ──POST /v1/chat/completions──→  ModelMux  ──POST /v1/chat/completions──→  Upstream A
  "model":"local/gemma4"                 │  rewrites "model":"gemma4"                    (vllm)
                                         │  injects default params
                                         │
                                      Upstream A unreachable?
                                         │
                                         └──POST /v1/chat/completions──→  Upstream B
                                             "model":"deepseek-v4-flash"          (deepseek)
```

### Capability Routing (image / audio)

ModelMux determines the required capability from the **current turn** (last message + request path) and searches the fallback chain for a matching model:

```
Receive request, parse "model" and body
        │
        ▼
① Detect required capability (last message + path only)
   ├─ path is /v1/images/*    → requires IMAGE
   ├─ path is /v1/audio/*     → requires AUDIO
   ├─ last message has image_url/input_image  → requires IMAGE
   └─ last message has audio_url/input_audio  → requires AUDIO
        │
        ▼
② Search fallback chain (capability + health filter)
   ├─ primary supports capability & healthy → use it
   ├─ unsupported/unavailable → recurse down fallback chain (incl. nested)
   └─ no match in whole chain → return capability_unavailable error
        │
        ▼
③ Strip historical multimodal content before forwarding (see below)
```

### Multi-turn Image/Audio Stripping

In multi-turn conversations, clients (e.g. hermes-agent) resend the **full history** with every request.
Images/audio from earlier turns were already interpreted and turned into text replies
(in prior turns), so they need not be re-sent. Before forwarding, ModelMux:

- **Strips** `image_url` / `input_image` / `audio_url` / `input_audio` blocks from
  every message's `content` array **except the last message**
- **Keeps text blocks** (the user's written question is preserved)
- **Keeps the last message intact** (current turn's new image/audio must be processed)

This way text-only upstreams (e.g. deepseek) won't reject later text turns with a 400
caused by stale image_url in history, while image turns still route to a multimodal model
with the new image preserved.

```
Turn 1 (text)   →  deepseek-v4-flash    ✓ text → text model
Turn 2 (image)  →  local/current        ✓ image → multimodal model (local)
Turn 3 (text)   →  deepseek-v4-flash    ✓ historical image stripped → back to text model
   └─ on forward: earlier image_url blocks removed, only text Q&A kept
```


## API Reference

### Authentication

All endpoints except `/health` require the unified API key:

```
Authorization: Bearer sk-your-unified-api-key
```

### Endpoints

| Endpoint | Method | Auth | Description |
|---|---|---|---|
| `/health` | GET | No | Service liveness |
| `/v1/models` | GET | Bearer | List configured models (from config.json) |
| `/v1/chat/completions` | POST | Bearer | Chat Completions (stream support) |
| `/v1/{**path}` | ANY | Bearer | Transparent proxy (embeddings, images, audio, etc.) |

### Error Response

```json
{
  "error": {
    "message": "No healthy routes found for model 'unknown-model'",
    "type": "no_route"
  }
}
```

## Configuration Reference

### appsettings.json

| Parameter | Type | Default | Description |
|---|---|---|---|
| `Kestrel.Endpoints.Http.Url` | string | `http://0.0.0.0:5000` | Listen address |
| `Kestrel.Limits.MaxConcurrentConnections` | int | 200 | Max concurrent connections |
| `ApiKey` | string \| string[] | — | Unified API key(s) (used by clients); a single string or an array of accepted keys |
| `RequestTimeoutSeconds` | int | 300 | Upstream request timeout (seconds) |
| `MaxRetryAttempts` | int | 3 | Max fallback attempts |
| `DebugPrompt` | bool | false | When true, logs message content at Debug level (privacy-sensitive) |
| `HealthCheck.Enabled` | bool | true | Enable background health checks |
| `HealthCheck.IntervalSeconds` | int | 30 | Polling interval (seconds) |
| `HealthCheck.TimeoutSeconds` | int | 5 | Health check request timeout (seconds) |
| `HealthCheck.UnhealthyCooldownSeconds` | int | 30 | Cooldown after marking unhealthy (seconds) |
| `Logging.LogLevel.Default` | string | `Information` | Log level |

### config.json

| Parameter | Type | Required | Description |
|---|---|---|---|
| `<provider>` | string | Yes | Provider namespace (e.g. `local`, `deepseek`, `openai`) |
| `<provider>[].baseUrl` | string | Yes | Upstream API URL (e.g. `https://api.deepseek.com`, with or without `/v1` suffix) |
| `<provider>[].apiKey` | string | Yes | Upstream API key (shared by all models under this endpoint group) |
| `<provider>[].headers` | object | No | Custom HTTP headers injected into every upstream request for this provider. Client-supplied headers of the same name take precedence. Sensitive headers (`Authorization`, `Host`, `Connection`, `Transfer-Encoding`) are protected and cannot be overridden. |
| `<provider>[].models` | array | Yes | Model entries under this endpoint group |
| `models[].modelid` | string | Yes | Upstream model name sent to the provider's API |
| `models[].alias` | string │ string[] | No | Public-facing name override(s): a single string `"x"` or an array `["x","y"]`. Each alias is exposed as `provider/alias` in `/v1/models` and routes to the same upstream `modelid`. When any alias is set, `modelid` itself is not exposed (list it explicitly as an alias if you also want it). Useful for exposing thinking/non-thinking variants of the same model. |
| `models[].type` | string[] | No | Capability set the model supports (e.g. `["text"]`, `["text","image"]`, `["text","image","audio"]`). Defaults to `["text"]` when omitted. Used for capability routing: when a request needs image/audio that the model lacks, ModelMux searches the fallback chain for a capable model. |
| `models[].defaultParams` | object | No | Default body parameters (number/string/bool/object). Injected when absent from client request |
| `models[].fallback` | string[] | No | Ordered list of fallback models in `provider/modelid` format |

### Custom Headers

You can inject custom HTTP headers into every upstream request for a provider by adding a `headers` object at the provider level:

```json
{
  "local": [{
    "baseUrl": "http://172.1.1.2:14850/v1",
    "apiKey": "=whatthefuck=",
    "headers": {
      "X-Title": "modelmux",
      "X-Custom-Header": "value"
    },
    "models": [...]
  }]
}
```

These headers are added to the upstream request after copying client headers. If the client already sends a header with the same name, the client's value is preserved and the configured value is ignored. Sensitive headers (`Authorization`, `Host`, `Connection`, `Transfer-Encoding`) are protected and cannot be set via this field.

### Example Configurations

**DeepSeek**:
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

**OpenAI**:
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

**Local vLLM**:
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

**Model Alias (thinking/non-thinking variants)**:
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
> **How alias works**: The alias becomes the public name (`local/qwen3-235b-thinking`), but the upstream API still receives `"model":"qwen3-235b"`. The difference is `enable_thinking: true` is injected into the request body.

## Deployment

### Systemd (recommended)

```bash
sudo cp publish/modelmux /opt/modelmux/
sudo chmod +x /opt/modelmux/modelmux

# First start auto-generates appsettings.json; use generateconfig for config.json.example
cd /opt/modelmux
sudo ./modelmux generateconfig
sudo cp config.json.example config.json
# Edit config.json and appsettings.json to set your real configuration
```

Create `/etc/systemd/system/modelmux.service`:

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
sudo journalctl -u modelmux -f   # view logs
```

### Docker

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish -c Release -o /app

FROM ubuntu:22.04
WORKDIR /app
COPY --from=build /app/modelmux .
EXPOSE 5000
ENTRYPOINT ["./modelmux", "serve"]
```

```bash
docker build -t modelmux .
docker run -d -p 5000:5000 \
  -v $(pwd)/config.json:/app/config.json \
  -v $(pwd)/appsettings.json:/app/appsettings.json \
  modelmux
```

## License

MIT
