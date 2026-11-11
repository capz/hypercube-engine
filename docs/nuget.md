# NuGet distribution

The package IDs are **Capz.Hypercube.Core** and **Capz.Hypercube.GameCube**.
The generic HyperCube.Core ID belongs to an unrelated project. Assembly names
and namespaces remain Hypercube.Core / Hypercube and Hypercube.GameCube.
Both packages declare GPL-2.0-only, matching DolphinDotNet.

Core is a portable .NET 8 library. GameCube adds the engine's graphics and
streaming adapters and depends on Core plus a specific published
DolphinDotNet.GameCube version. The DolphinDotNet MSBuild SDK/compiler is a
separate application requirement; it is not copied into an engine library nupkg.
`Directory.Build.props` pins the package dependency version to
`DolphinDotNet.GameCube` **0.1.0-preview.302.1**. The matching compiler/Assets
preview includes indexed unlit vertex-color submission and shared mesh color
streams. `IndexedMeshCache` is included in this engine preview; ordered passes
retain the portable implementation unless explicitly built for the native backend. Source validation
continues to use the pinned SDK checkout from `.sdk-revision`.

## Build and test without publishing

```sh
python3 eng/pack.py --version 0.1.0-preview.1
python3 tests/nuget_consumer.py --workspace '/tmp/external engine consumer' --native
```

The packer builds against the public SDK package, checks package IDs, versions,
license/readme/PDB contents and dependency metadata, and emits packages.json with
the source commit. It fails on an accidental source-checkout SDK dependency.
The consumer relocates the nupkgs, uses an isolated NuGet cache outside the
repository, executes Core, then builds the Rendering sample through PackageReference
and the published DolphinDotNet SDK. It verifies transitive dependencies, confirms the SDK packages match the reviewed
source revision in `.sdk-revision`, and checks AOT bundling and, with --native,
actual DOL/ELF output. No SDK source checkout is
needed to consume the packages.

## Publish

The NuGet preview workflow verifies packages and native consumption before an
upload. PRs only verify. Explicit `nuget-preview-*` tags request preview
publication, including before the packaging PR is merged; manual publish=true
is restricted to main. Versions are `0.1.0-preview.RUN.ATTEMPT`; rerunning
verification produces a new version. Both packages from a run share the version.
The publishing job downloads the exact verified artifacts rather than rebuilding.
Retrying only publication reuses those artifacts and skips existing versions,
so a partial upload can resume without failing on the first package again.

Trusted publishing needs a policy on NuGet.org for account **capz**:

- Repository owner: capz; repository: hypercube-engine.
- Workflow: nuget-preview.yml; environment: nuget-preview.
- Authorize both new Capz.Hypercube package IDs (and their ownership/creation).

GitHub OIDC supplies a short-lived publishing credential through NuGet/login.
No API key belongs in source, chat, or the engine packages. Repository environment
protection, if configured by its owner, still applies. A missing NuGet trusted
publisher policy blocks the publish job even when GitHub tests pass. The workflow
cannot create that external account policy on its own.

Application consumption:

```xml
<Project Sdk="DolphinDotNet.Sdk/SDK_VERSION">
  <ItemGroup>
    <PackageReference Include="Capz.Hypercube.GameCube" Version="ENGINE_VERSION" />
  </ItemGroup>
</Project>
```

Use the SDK version pinned by that engine release. Desktop applications need
only Capz.Hypercube.Core and the normal Microsoft.NET.Sdk.
