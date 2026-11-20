# 用 DeepSeek Harness 接游戏电话

## 第一次使用

下面假定你已经可以在 Windows 上正常使用 DeepSeek Harness。

1. 双击「启动电话.bat」，选择游戏目录和「DeepSeek Harness」。如果能自动找到 DSH，可直接点「测试连接」。否则点「复制接入说明」，粘贴给你平时使用的 DSH，等它配置好再点「启动电话服务」。启动器按实际端口生成 MCP 补丁，并让无界面模式读取 DSH 已保存的凭据。
2. 进已有存档，从电话簿打第一通，先用文字测试。收到回复后选一句，确认能续上前文。每个存档会绑定自己的 DSH 会话。切回 Codex 时使用各自的会话历史。

## 已有测试配置

「开始测试.bat」继续读取自己的 `test-config.json`。原先的手写模板可以继续用。重新配置时可把 `test-config.example.json` 复制为 `test-config.json`，把 `client` 改为 `dsh`，并确认 `gameDirectory` 指向实际游戏。可用 `clientExecutable` 指定程序路径。

高级手动配置可参考 [DSH 的 MCP 样例](dsh-mcp.example.yml)。建新会话用 `dsh --profile headless --json -`，续跑增加 `--session-id {session}`，两条命令都加载同一份 MCP 补丁。DSH 在 stdout 输出 `sessionId`，台词通过 MCP 回到游戏。续跑需要保持相同工作目录。[DSH Headless 文档](https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/bundle/headless/README.md)

## 接不通时

- 在 PATH 中找不到 DSH 时，把「复制接入说明」的内容交给 DSH。它可以把命令入口配置为 `clientExecutable`，源码版可填 `apps/cli/lib/bin.js` 的完整路径，并在 `clientNodeExecutable` 指定配套的 Node。
- 缺模型凭据时，回 DSH 核对 Models 设置。也可从启动器的父进程环境提供 `DEEPSEEK_API_KEY`。
- MCP 初始化失败时，检查使用的地址和 Bearer 口令，手写模板还要确认两个命令都加载同一份补丁。[DSH MCP 客户端文档](https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/mcp/mcp-client/README.zh.md)
- 第一通能回、选项接不上时，查 `.phone-test-state` 的日志，并确认续跑模板保留了 `--session-id {session}`。
