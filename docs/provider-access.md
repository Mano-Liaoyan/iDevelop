# Provider access reference

Checked on 2026-10-03. These findings describe source code and provider documentation. No account login, subscription quota, paid request, or live provider integration was tested.

## Pi AI source snapshot

The inspected Pi commit is `4c6fb7cfe8c538a668726f6f8b3554098c39faee`. Its AI package is `@earendil-works/pi-ai`, version `1.0.1`, with Node `>=22.19.0` and MIT licensing. Cached repository pages showed older contents, so the inventory below uses the pinned provider factories and types. [Package metadata](https://github.com/earendil-works/pi/blob/4c6fb7cfe8c538a668726f6f8b3554098c39faee/packages/ai/package.json), [license](https://github.com/earendil-works/pi/blob/4c6fb7cfe8c538a668726f6f8b3554098c39faee/LICENSE)

Pi AI provides model discovery, provider authentication, request dispatch, streaming, and a common conversation representation. It does not implement iDevelop's graph scheduler or provide every coding client's agent loop. Its TypeScript implementation can be studied without selecting TypeScript for iDevelop. [Model dispatch](https://github.com/earendil-works/pi/blob/4c6fb7cfe8c538a668726f6f8b3554098c39faee/packages/ai/src/models.ts), [authentication types](https://github.com/earendil-works/pi/blob/4c6fb7cfe8c538a668726f6f8b3554098c39faee/packages/ai/src/auth/types.ts)

## Requested providers

Browser login, coding-plan keys, ordinary API keys, and an official coding client's saved account are separate access modes. Provider implementation in Pi does not establish permission for a new application's use of that provider's subscription.

| Provider | Subscription or coding-plan access | API access and Pi status |
| --- | --- | --- |
| OpenAI and Codex | Official Codex supports ChatGPT authentication. OpenAI also documents third-party Sign in with ChatGPT with the granted `chatgpt.tokens.use.direct` permission. Identity-only login is insufficient. | Pi's `openai` supports API keys and the new OAuth flow. `openai-codex` is labelled legacy. Use the documented grant or official local Codex integration. [Codex authentication](https://developers.openai.com/codex/auth), [third-party sign-in](https://developers.openai.com/cookbook/articles/sign-in-with-chatgpt) |
| Anthropic and Claude Code | Claude Code and Agent SDK provide documented subscription routes. The current notice pauses the June 15 change and says SDK, `claude -p`, and third-party SDK usage still draw from subscription limits. | Pi has `anthropic` API and OAuth mechanisms. Its direct OAuth code is not proof of permission to use that route in iDevelop. Prefer official Code or SDK integration for subscriptions. Direct API requests use API credentials. Recheck the current policy before release. [SDK notice](https://support.claude.com/en/articles/15036540-use-the-claude-agent-sdk-with-your-claude-plan), [authentication policy](https://support.claude.com/en/articles/13189465-log-in-to-your-claude-account) |
| Google and Gemini | Antigravity CLI (`agy`) serves Google AI Pro, Ultra, and no-cost individual accounts. Gemini CLI stopped serving those accounts on June 18, 2026. It remains available for Gemini Code Assist licenses and paid API keys. | Pi's `google` uses a Gemini API key. `google-vertex` uses Google Cloud credentials. Google account OAuth is absent from this pinned inventory, so subscription execution needs a local Antigravity CLI adapter. Gemini API has separate billing. [Transition notice](https://github.com/google-gemini/gemini-cli/discussions/27274), [API billing](https://ai.google.dev/gemini-api/docs/billing) |
| xAI and Grok | Official Grok Build documents headless execution and ACP. xAI documents subscription access in Kilo Code. | Pi's `xai` implements API-key access and subscription-labelled device OAuth. Approval of Pi's OAuth client or a custom iDevelop client was not established. Grok Build and ordinary API access have clearer documented routes. [Grok Build](https://docs.x.ai/build/overview), [Kilo integration](https://x.ai/news/grok-kilocode), [API billing distinction](https://docs.x.ai/console/faq/accounts) |
| Alibaba and Qwen | Coding Plan and Token Plan use dedicated keys and endpoints. Token Plan Team uses assigned seats with individual keys. Its policy permits interactive coding tools and restricts automated scripts and application backends. Qwen Code's former OAuth free tier has ended. | Pi includes three Qwen Token Plan configurations. Normal Model Studio API access and other coding endpoints need explicit configuration. Whether a particular iDevelop workflow qualifies as interactive coding remains unverified. [API keys](https://www.alibabacloud.com/help/en/model-studio/get-api-key), [Token Plan Team](https://docs.modelstudio.console.alibabacloud.com/en/model-studio/token-plan-team-overview), [Qwen Code authentication change](https://qwenlm.github.io/qwen-code-docs/en/users/support/troubleshooting/) |
| ByteDance, Volcengine, and Doubao | Ark documents separate personal and enterprise Coding Plans using dedicated credentials. | No Volcengine, Ark, or Doubao built-in appears in Pi's pinned registry. A custom OpenAI-compatible provider is a candidate for the documented endpoint, subject to compatibility and access checks. China Volcengine evidence does not establish equivalent BytePlus international access. [Personal plan](https://docs.volcengine.com/docs/ark/coding-plan-personal-plan-overview?lang=zh), [enterprise configuration](https://docs.volcengine.com/docs/ark/coding-plan-enterprise-ai-other-tools?lang=zh) |
| DeepSeek | No subscription integration was verified. | Pi's `deepseek` uses `DEEPSEEK_API_KEY` with the ordinary API. Provide API-key access initially. [DeepSeek documentation](https://api-docs.deepseek.com/) |
| Z.ai and GLM | GLM Coding Plan explicitly lists Pi as a supported tool. Subscriber and supported-tool restrictions still apply. | Pi's `zai` and `zai-coding-cn` target distinct global and China coding endpoints with keys. A new client using only the Pi AI library is not automatically the supported Pi product. Ordinary API billing needs its correct endpoint. [Supported tools](https://docs.z.ai/devpack/tool/others), [usage policy](https://docs.z.ai/devpack/usage-policy), [FAQ](https://docs.z.ai/devpack/faq) |
| Moonshot and Kimi | Kimi Code documents coding keys for third-party tools and self-built applications. Official clients use OAuth. | Pi's `kimi-coding` has coding-key and device-OAuth implementations. Approval to reuse its OAuth client was not verified. `moonshotai` and `moonshotai-cn` provide separate regional API configurations. Kimi membership and ordinary platform billing are distinct. [Membership integrations](https://www.kimi.com/en/help/kimi-code/membership-guide), [platform and region distinctions](https://www.kimi.com/help/kimi-api/api-troubleshooting) |

## Complete built-in provider inventory

The pinned `KnownProvider` definition and `builtinProviders()` registry contain the following 42 identifiers. Regional endpoints, gateways, and plan variants have separate identifiers. Radius uses dynamic discovery, so a static model catalog alone does not enumerate all runtime capabilities. [Provider types](https://github.com/earendil-works/pi/blob/4c6fb7cfe8c538a668726f6f8b3554098c39faee/packages/ai/src/types.ts), [factory registry](https://github.com/earendil-works/pi/blob/4c6fb7cfe8c538a668726f6f8b3554098c39faee/packages/ai/src/providers/all.ts)

```text
amazon-bedrock
ant-ling
anthropic
azure-openai-responses
baseten
cerebras
cloudflare-ai-gateway
cloudflare-workers-ai
deepseek
fireworks
github-copilot
google
google-vertex
groq
huggingface
kimi-coding
meta
minimax
minimax-cn
mistral
moonshotai
moonshotai-cn
nvidia
openai
openai-codex
opencode
opencode-go
openrouter
qwen-token-plan
qwen-token-plan-cn
qwen-token-plan-individual
radius
together
typesafe
vercel-ai-gateway
xai
xiaomi
xiaomi-token-plan-ams
xiaomi-token-plan-cn
xiaomi-token-plan-sgp
zai
zai-coding-cn
```

Pi implements OAuth loaders for Anthropic, OpenAI ChatGPT, legacy OpenAI Codex, GitHub Copilot, OpenRouter, Kimi Code, Meta, xAI, and Radius. OpenRouter's OAuth flow produces an API key. Other configurations generally use stored credentials, environment keys, or cloud identity. This is an implementation inventory, not a claim that every route consumes a consumer subscription. [OAuth loader](https://github.com/earendil-works/pi/blob/4c6fb7cfe8c538a668726f6f8b3554098c39faee/packages/ai/src/auth/oauth/load.ts), [environment credentials](https://github.com/earendil-works/pi/blob/4c6fb7cfe8c538a668726f6f8b3554098c39faee/packages/ai/src/env-api-keys.ts)

## Cross-provider context limits

Pi converts conversation history for the destination model. It preserves compatible reasoning signatures only for the same provider, API, and model. It can convert visible thinking to ordinary text while dropping opaque or redacted data, normalize tool identifiers, and account for missing tool results. These conversions do not transfer an official client's private process state or guarantee that a new model interprets previous work identically. [Message transformation](https://github.com/earendil-works/pi/blob/4c6fb7cfe8c538a668726f6f8b3554098c39faee/packages/ai/src/api/transform-messages.ts)

The proposed iDevelop handoff therefore relies on task instructions, observed actions, verified code revisions, artifacts, concise decisions, and next steps. It does not require access to hidden reasoning or a provider's opaque session state.

## Unverified items

- Custom-client eligibility for subscription OAuth routes not explicitly covered by provider documentation.
- Whether generated or unattended workflows meet each coding plan's permitted-use requirements.
- Account-specific models, regional availability, quotas, and current reasoning options.
- Authentication refresh, cancellation, rate limiting, and recovery on real local runners.
- Volcengine compatibility through a custom provider and any international BytePlus equivalent.

These gaps are product integration work. They do not prevent designing the local graph editor or optional team synchronization.
