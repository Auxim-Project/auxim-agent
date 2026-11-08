# Auxim Agent

Auxim is a modular runtime environment for AI agents. Its long-term direction is
to provide reusable foundations for agent applications. The current Early Alpha
includes the Kernel libraries and a default Agent with a terminal CLI.

Auxim Agent is the default agent implementation for Auxim, with a .NET library
and a terminal CLI. It connects model responses and tool calls to the execution
environment provided by [Auxim Kernel](https://github.com/Auxim-Project/auxim-kernel).

Agent handles model communication and the conversation loop. Kernel provides
runtime services, built-in tools, resource approvals, session storage, and the
virtual filesystem (VAFS). The `Auxim.Kernel` NuGet package supplies the
`Auxim.Core`, `Auxim.Tools`, and `Auxim.VAFS` assemblies.

## Features

- Interactive terminal with slash commands, conversation history, and session switching.
- OpenAI-compatible model client with streaming responses and tool calling.
- Built-in tools for files, search, Git, restricted shell commands, web fetching,
  and task lists, executed through Kernel's runtime and approval flow.
- CLI commands for model settings, credentials, sessions, VAFS mappings,
  resource approvals, and diagnostics.
- A local echo client for trying the CLI without a model service.

## Requirements

- .NET 10 SDK and Git.
- Access to NuGet.org for dependency restore.
- For model-backed chat: an OpenAI-compatible service, a model available on that
  service, and an API key if the provider requires one.

The project currently references
[`Auxim.Kernel` 0.1.0-alpha.1](https://www.nuget.org/packages/Auxim.Kernel/0.1.0-alpha.1),
which is available on NuGet.org. A separate Kernel checkout or local package
feed is not needed for the steps below.

## Quick Start

Clone the repository, then restore and build:

```bash
git clone https://github.com/Auxim-Project/auxim-agent.git
cd auxim-agent
dotnet restore Auxim.Agent.sln --source https://api.nuget.org/v3/index.json
dotnet build Auxim.Agent.sln -c Release --no-restore
```

Start with commands that need no API key or model service:

```bash
dotnet run --project src/Auxim.Cli -c Release --no-build -- help
dotnet run --project src/Auxim.Cli -c Release --no-build -- tools
dotnet run --project src/Auxim.Cli -c Release --no-build -- doctor
dotnet run --project src/Auxim.Cli -c Release --no-build -- tool run echo text=hello
```

`help` lists command usage; `tools` lists registered tools without running them.
`doctor` reports configuration, credentials, and runtime settings; missing config
or credentials are expected on a fresh setup. The last command runs the echo tool
directly and returns `hello`.

To open the interactive terminal, omit the command:

```bash
dotnet run --project src/Auxim.Cli -c Release --no-build
```

Use `/help` for interactive commands and `/exit` to quit. With no configuration,
the default `local` provider only echoes input; it does not run a language model.

## CLI Commands

Pass these arguments after `--` in the `dotnet run` command shown above:

| Arguments | Purpose |
| --- | --- |
| `help` | Show command usage. |
| `tools` | List available tools. |
| `tool run echo text=hello` | Run a tool directly, without a model. |
| `doctor` | Show local diagnostics; this does not test model connectivity. |
| `model show` | Show the selected provider, model, and base URL. |
| `auth status` | Show credential status without displaying the key. |
| `config show` | Show the current configuration. |
| `session list` | List saved sessions. |
| `sandbox show` | Show VAFS workspace and volume mappings. |
| `approval list` | List stored resource approval grants. |
| `chat "hello"` | Send a message using the configured provider. |

Configuration and credentials are stored under `~/.auxim` by default, in
`config.json` and `.env`. Set `AUXIM_HOME` to use a different directory. Resource
approval prompts require an interactive terminal.

### Connect a model service

For chat, nonblank `AUXIM_PROVIDER`, `AUXIM_MODEL`, and `AUXIM_BASE_URL` values
override `model.provider`, `model.name`, and `model.baseUrl` in `config.json`,
respectively. These variables can come from the process environment or `.env`.
Existing process variables take precedence: `.env` only populates variables that
are not already set.

Have your provider's base URL, model ID, and any required credentials ready.
Run the setup wizard and choose values supported by your service:

```bash
dotnet run --project src/Auxim.Cli -c Release --no-build -- model set
```

The wizard can save a required API key. To add or replace it separately, use the
credential prompt after selecting the provider:

```bash
dotnet run --project src/Auxim.Cli -c Release --no-build -- auth set-api-key
dotnet run --project src/Auxim.Cli -c Release --no-build -- chat "hello"
```

Chat uses the configured endpoint. Model responses and tool calling depend on
the selected service's capabilities; the local echo client does not exercise them.

## Development

- [src/Auxim.Agent](https://github.com/Auxim-Project/auxim-agent/tree/main/src/Auxim.Agent): agent library and model clients.
- [src/Auxim.Cli](https://github.com/Auxim-Project/auxim-agent/tree/main/src/Auxim.Cli): CLI and interactive terminal.
- [tests/Auxim.Agent.Tests](https://github.com/Auxim-Project/auxim-agent/tree/main/tests/Auxim.Agent.Tests): agent and model client tests.

After restoring and building, run the tests:

```bash
dotnet test Auxim.Agent.sln -c Release --no-build --no-restore
```

The current tests use injected clients and HTTP handlers and do not require a
live model service or API key.

## Project Status

**Early Alpha.** APIs, CLI behavior, and configuration formats may change.
CI is configured for Linux, Windows, and macOS; terminal behavior and external
tool availability can vary by environment.

## License

[Apache License 2.0](https://github.com/Auxim-Project/auxim-agent/blob/main/LICENSE).
