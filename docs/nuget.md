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
`DolphinDotNet.GameCube` **0.1.0-preview.318.1**. The matching compiler/runtime
source is **4c8cd26a1b5f8003598463116960367ffa17418f**, also pinned in
`.sdk-revision` for source validation. Ordered passes retain their native SDK
backend by default; the portable comparison remains available through
`HypercubeNativePasses=false`.

This upgrade accompanies [AnimalTown #47](https://github.com/capz/AnimalTown/pull/47).
SDK 318 exposes independent TEV alpha stages, two-reference GX comparisons and
`TevMaterialPresets.DualTextureGlints` for its two scrolling water samples.
Hypercube's scrolling helpers use these materials through the existing SDK API;
no separate effect format, shader wrapper or texture allocation is introduced.
The relocated package consumer builds/AOT-bundles this combination with a single
engine reference and no runtime override or NU1608 suppression.

Once this engine preview has been verified and published, AnimalTown can pin
that published version in place of engine 28 and remove its temporary explicit
runtime-318 compatibility override. Engine previews are published only by the
existing release workflow after its publication trigger; this PR does not
reserve a future package number or publish an unverified package.

## Build and test without publishing

```sh
python3 eng/pack.py --version 0.1.0-preview.1
python3 tests/nuget_consumer.py --workspace '/tmp/external engine consumer' --native
```

The packer builds against the public SDK package, checks package IDs, versions,
license/readme/PDB contents and dependency metadata, and emits packages.json with
the source commit. It fails on an accidental source-checkout SDK dependency.
The consumer relocates the nupkgs, uses an isolated NuGet cache outside the
repository, executes Core, then builds the Rendering sample and water-material consumer through PackageReference
and the published DolphinDotNet SDK. It verifies transitive dependencies, confirms the SDK packages match the reviewed
source revision in `.sdk-revision`, and checks AOT bundling and, with --native,
actual Rendering and water DOL/ELF output. No SDK source checkout is
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
