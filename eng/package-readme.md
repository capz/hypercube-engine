# Hypercube Engine

Experimental GameCube engine built on DolphinDotNet. GPL-2.0-only.

- **Capz.Hypercube.Core**: portable .NET 8 culling, bounded draw queues, resource
  scheduling/leases, buffer growth, material-motion helpers and frame metrics.
- **Capz.Hypercube.GameCube**: DolphinDotNet adapters for batching, reusable meshes,
  shared textures/effects, cloud layers, streaming and graphics telemetry.

```sh
dotnet add package Capz.Hypercube.Core --prerelease
# Or, in a GameCube project:
dotnet add package Capz.Hypercube.GameCube --prerelease
```

GameCube depends on Core and DolphinDotNet.GameCube. Use a GameCube application
with `DolphinDotNet.Sdk/0.1.0-preview.245.1` and a matching
`DolphinDotNet.GameCube` package. These engine packages contain managed libraries
and PDBs, not the native compiler or a replacement SDK. Build the application with
the DolphinDotNet SDK to produce a GameCube DOL. Do not execute graphics adapters
as a normal desktop .NET application; native graphics calls require the console.

Namespaces remain `Hypercube` and `Hypercube.GameCube`. Package names use the Capz
prefix to distinguish this engine from an unrelated HyperCube.Core package.

[Source, samples and build instructions](https://github.com/capz/hypercube-engine)
· [Shared rendering API contracts](https://github.com/capz/hypercube-engine/blob/main/docs/shared-rendering.md)

This is a preview. Emulator correctness is not a physical GameCube performance
measurement. DolphinDotNet is separately distributed under GPL-2.0-only.
