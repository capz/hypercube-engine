# Selecting CI checks

Pull requests select checks from the **entire PR diff**, using its actual target
branch and merge base. A documentation commit after a runtime commit cannot
hide the earlier runtime changes. Renames are examined as both a deletion and
an addition; filenames containing whitespace are preserved.

The selector lives in `tools/ci_changes.py`. Its reusable workflow runs its own
regression tests before publishing a plan. Every workflow reports its selection
in the job summary. Selection does not use GitHub workflow `paths` filters, so
required checks still report for documentation-only PRs.

| Change | Selected checks |
| --- | --- |
| README or documentation/evidence only | Selector tests and required-check reporting |
| Existing host unit tests | Fast host suite |
| Rendering helpers or rendering sample | Host, Debug/Release rendering capture, NuGet consumer |
| World streamer/cache | Host, Debug/Release packed scene, four Release comparisons, NuGet consumer |
| Texture family/LOD policy | Host, Debug/Release family contracts, NuGet consumer |
| Performance proof tool | Host and performance comparison |
| Profiling proof tool | Host and the three profiling comparisons |
| Common Dolphin decoder | Host and all emulator lanes |
| Package authoring/consumer/license | NuGet packaging and relocated native consumer |
| Shared core, SDK pin, project/build properties, CI plumbing, unknown path | Full suite |

Rules combine across all changed paths. New runtime files and unclassified paths
run the full suite until their consumers are reviewed and a narrower rule is
added. Missing/malformed event data, unavailable commits or failed diff reads
also run the full suite. The selector tests cover these fallbacks and real Git
histories with multiple commits, changed target branches, deletes and renames.

Fast host checks run once for executable changes: all Python contracts, the
portable engine tests in Debug/Release, the indexed-cache checks, and both native
and fallback ordered-pass contracts. Console builds depend on that job and skip
its duplicate host work. `tools/build_validation.sh` retains all original host
tests when run locally; its CI-only `HYPERCUBE_SKIP_HOST_TESTS=1` setting is used
only behind the required host job. Asset generation still runs for every console
build because each isolated job needs its own pack/disc.

The four long Release comparisons use separate jobs with their original build
flags, capture durations and assertions. Only selected rows enter the comparison
matrix. Rendering and packed-scene runs are independent lanes, allowing a
rendering-only change to avoid world captures and profiling.

The stable `validation (Debug)` and `validation (Release)` checks always run.
They require selection to succeed, every selected validation job to succeed and
every unselected job to be skipped. Failures, cancellations, missing results or
unexpected skips fail these gates. Family and package checks report separately.

Feature branches are validated through their PR, avoiding duplicate push and PR
emulator runs. Pushes to `main` and manual workflow runs execute full validation;
NuGet release tags always run full package verification. Use **Run workflow** on
any workflow to force its full set of checks without changing selection rules.
No capture window or correctness assertion is reduced by selection.

When changing routing, run:

```sh
python3 -m unittest discover -s tests -p test_ci_changes.py
python3 tools/test_host.py
```

Keep the explicit dependency rules and required-check tests in sync with new
runtime consumers. A rule should be narrowed only when all affected consumers
are covered by the selected lanes.
