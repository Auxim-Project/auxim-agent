# Auxim.Agent

Auxim.Agent is the official default agent implementation for Auxim. It runs on
the trusted runtime and resource environment provided by Auxim Kernel.

This repository contains the `Auxim.Agent` library, the `Auxim.Cli` application,
and agent tests. The library depends on the `Auxim.Kernel` NuGet package,
which provides the `Auxim.Core`, `Auxim.Tools`, and `Auxim.VAFS` assemblies.

## Build and test

.NET 10 SDK is required. Restore needs `Auxim.Kernel` version
`0.1.0-alpha.1` from an available NuGet source. Until that package is
published, a temporary local package feed can provide it.

```bash
dotnet restore Auxim.Agent.sln
dotnet build Auxim.Agent.sln -c Release --no-restore
dotnet test Auxim.Agent.sln -c Release --no-build
```

Auxim.Agent is in early alpha. See [LICENSE](LICENSE) for the Apache 2.0 license.
