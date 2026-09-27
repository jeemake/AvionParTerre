# Efficiency and LLM integration review

Reviewed 27 September 2026 against `main` commit `5dde6dd38ad12132d7d9bec734558e0ef7f3dd58`.

## Assessment

The existing split between a Revit-independent Core and a Revit adapter is sound. Deterministic model operations should remain deterministic; the model should propose bounded decisions about genuinely missing information. The largest practical improvements are safer thread boundaries, accurate decision context, smaller requests, bounded concurrency, and evidence-based validation. Adding another LLM to review every answer would increase latency and still would not prove a specification correct.

This change implements a first set of fixes, adds regression coverage and a repeatable synthetic benchmark, and leaves a prioritized roadmap for work that requires Revit geometry fixtures, domain decisions, or live model evaluation. It is not a certification of construction specifications or a complete security audit. No live LLM credentials or Revit runtime were available.

## Implemented fixes

| Priority | Finding and code area | Change |
|---|---|---|
| Critical | `Autopilot.ConsultAsync` called `FicheGenerator.Input` and `ExistingLabel` on the worker thread. These read `FamilySymbol`, parameters, categories, and sheet numbers. | Capture plain joinery inputs during `Analyze` on Revit's API thread. Consultation uses the snapshot. |
| High | `AdviseRoomsAsync` deduplicated same-named rooms using known finish **keys**, excluding their values, family, level, area, and candidates. | Deduplicate only identical decision contexts, with collision-resistant JSON signatures. Complete rooms incur no LLM call. More calls for genuinely different rooms are intentional. |
| High | Model answers could overwrite an existing room family or choose a contradictory profile. | Preserve input family, reject incompatible profile decisions, protect known finish supports, and reject unsupported code-like finish values. |
| High | `JsonExtract.Str` converted arrays/objects into strings; scalar array entries could throw. Missing/duplicate/foreign IDs were silently ignored or matched to the first answer. | Validate response shape, text types, requested IDs, completeness, and uniqueness before consuming a batch. Structured schemas are also sent to compatible providers. |
| High | Same joinery mark could refer to different families; advice was applied to the first match. | Ambiguous joinery types are excluded from AI decisions and reported. Core also rejects duplicate input marks. |
| High | HTTP success with empty choices, refusals, truncated output, or embedded errors could be mistaken for usable content. | Fail closed with a controlled `LlmException`; do not apply partial provider responses. |
| High | An AI-selected profile populated the whole finish grid, including preserved fields. Unsaved user edits were not included in the AI context. | Update profile metadata without copying its values. Send edited grid values as known context; apply only validated missing supports. |
| High | `FinishService.Apply` recorded proposed finish applications even when preserving a different manual value; an equal value could become plugin-owned. | Build export applications from actual post-write values, including read-only/preserved supports; retain manual ownership. |
| Medium | Steps enabled in the launch dialog had not been analyzed; old work could persist when re-analyzing. | Re-analyze only when selected steps changed, resetting the analysis collections. |
| Medium | Rejecting a classification could still persist it via its finish assignment; logs did not distinguish rejected proposals. | Remove the dependent finish assignment and record review status in JSON/CSV. Review status is distinct from execution success. |
| Medium | An oversized but short view could share its page despite the packer's documented behavior. | Reserve that page exclusively for the oversized item, with a regression test. |

## Implemented performance improvements

- Classified-room prompts include matching families and explicitly nominated candidates, rather than every profile. Unclassified rooms retain the full profile set so retrieval does not suppress possible classifications. Send finish options only for supports missing somewhere in the batch. Precompute profile summaries and normalized finish lookups per advisor.
- Send two independent batches concurrently by default; configurable range 1–4. Preserve deterministic result order and synchronize usage accounting. Revit operations remain serial.
- Batch sizes are bounded to 1–20; output budgets scale with batch size instead of always requesting 8,000 tokens. Library selection uses 512 tokens.
- Validate locally on the successful path: **no second model call for a valid batch**. One repair request is permitted for invalid JSON/shape/IDs, within the same time budget. Catalog/domain conflicts are conservatively rejected and reported instead of starting an unbounded debate.
- A 120-second default budget covers each logical batch, including repair and transport retries. User cancellation propagates. One retry is allowed for selected transient HTTP statuses. A long `Retry-After` returns control instead of silently waiting or retrying too early. Network failures are not blindly retried because the original request may already have reached the provider.
- Cache explicit unsupported-format rejection for the client session; continue with local validation. Authentication, credit, malformed-schema, and policy failures do not trigger format downgrade. Connection pooling is shared across client instances; credentials remain on each client.
- Collect room membership once when building context and selecting occupied levels. Cache room names, classifications, parameter values, and level/name groups per `RoomDataService.Propose` invocation. Collect floors/ceilings lazily once per invocation rather than once per unresolved room. Bounding-box/geometry checking is still a remaining optimization and accuracy task.
- Run carnet and joinery production as batches, reusing generators/resources and retaining per-item transactions, progress and cancellation. Existing agency sheets stay protected.
- Hash PDFs from a file stream rather than loading the whole PDF into memory.
- Add request counts, repair counts, cumulative request duration, and review status to the decision log. Duration is **summed request time**, not wall time when requests overlap. HTTP attempts and failures are not yet separately accounted.

Advanced settings in the existing user settings JSON:

| Setting | Default | Meaning |
|---|---:|---|
| `taille_paquet` | 20 | Maximum distinct contexts per batch, clamped to 1–20 |
| `requetes_paralleles` | 2 | Independent batches in flight, clamped to 1–4 |
| `delai_paquet_secondes` | 120 | Logical batch deadline, clamped to 1–300 seconds |

The existing JSON serializer uses snake_case. These new settings do not require changing the settings file; defaults apply when absent. They are not new controls in the settings window.

## Verification and measured limits

- Core compiled with .NET SDK 8.0.408's Roslyn compiler and .NET 8 reference assemblies, including the regular-expression source generator.
- 90 existing/new xUnit test cases passed using the real xUnit assertion library and a temporary reflection runner. The sandbox cannot expose the process metadata required by the normal `dotnet` CLI/MSBuild startup, so this was **not** a normal `dotnet test` run. A GitHub Actions workflow runs the normal test project outside that environment.
- Added tests cover grouping, complete-room skipping, malformed responses, missing/duplicate IDs, bounded repair, protected decisions, fabricated citations, concurrency, cancellation, joinery ambiguity, HTTP format fallback, refusal/truncation, transient failures, long `Retry-After`, timeouts, and oversized-page isolation.
- Revit/WPF adapter changes require compilation against Revit 2025 on Windows and in-app smoke tests. Core tests cannot validate Revit thread context, transactions, view geometry, or WPF behavior.
- All 53 C# files passed a Roslyn syntax check, including the Revit adapter (syntax checking is not a Revit build).
- No live OpenRouter request was made. No production latency, model accuracy, or monetary saving has been measured.

Synthetic comparison of the same 80 distinct administration rooms, batch size 10, with a fake 80 ms provider delay per request:

| Metric | Baseline | Changed |
|---|---:|---:|
| Accepted room responses | 80 | 80 |
| LLM requests | 8 | 8 |
| Total message characters | 139,668 | 61,868 |
| Peak concurrent requests | 1 | 2 |
| Observed wall time | 687 ms | 374 ms |
| Validation errors | 0 | 0 |

This is 55.7% less message text and 45.6% less elapsed time in one synthetic run. Character counts exclude response-schema overhead and are not token counts. These results measure scheduler behavior and request construction, not architectural correctness or real provider throughput. The fake returns valid IDs and abstains from prescribing finishes.

Reproduce on a normal .NET 8 installation:

```sh
dotnet test tests/AvionParTerre.Core.Tests/AvionParTerre.Core.Tests.csproj --configuration Release
dotnet run --project tools/AvionParTerre.Benchmarks --configuration Release
```

## Prioritized next work

| Priority | Improvement | Why it matters / acceptance criterion |
|---|---|---|
| P0 before release | Windows Revit 2025 build and smoke test on a copy of LESE TEST | Exercise finishes, individual tasks, complete autopilot, cancellation, relaunch/idempotency, rejected decisions, and one-step undo. Confirm no Revit API calls occur during background consultation. |
| P1 accuracy | Geometry-backed HSP/HSD with explicit provenance | `RoomDataService` copies modal heights from similar rooms/levels and uses bounding-box containment for slabs/ceilings. These are inferences, not measurements. Handle room offsets, openings, sloped elements and multiple ceilings; show inferred heights as proposals requiring review. Validate against manually measured fixtures before automatic application. |
| P1 accuracy | Ground joinery specifications in actual parameters and approved agency clauses | The current input includes family/category/dimensions, but not fire/acoustic ratings, glass buildup, hardware, material, handing or detailed manufacturer evidence. The model can still invent prose prescriptions. Extract available values on the Revit thread, select versioned approved clauses, and leave unsupported performance claims undecided. |
| P1 accuracy | Architect-reviewed evaluation set | Build 50–100 representative and difficult cases: wet rooms, plant rooms, mixed uses, ambiguous abbreviations, conflicting existing values, repeated names across floors, duplicate joinery marks, long responses and prompt-injection strings. Score wrong decisions separately from appropriate abstention. Target zero overwrites of existing decisions and zero wrong-element assignments. |
| P1 integration | Explicit decision dependencies and execution outcomes | Model proposal → accepted/rejected → applied/failed/rolled back per element/support. Library/profile rejection should recompute dependent choices or reject the dependent set. Current review flags do not constitute proof of application. Avoid reporting items created inside rolled-back transactions as successful. |
| P1 latency | Evaluate model routing on identical fixtures | Compare available OpenRouter models using p50/p95 total latency, first-pass validity, semantic score, token cost, repair rate and abstention rate. Choose defaults from measured results; a model name or larger model alone does not establish accuracy. Verify configured model availability/capabilities and cache that metadata. |
| P2 latency | Targeted evidence retrieval before web search | Unknown room names currently enable web search for their batch. Prefer an approved agency glossary/local catalogue; query the web only for unresolved questions with a small search budget. Do not treat a retrieved URL as proof of a specification. |
| P2 latency | Context-aware cache of approved decisions | Key by normalized inputs + existing values + catalogue/profile version + model/prompt version + project scope. Invalidate on relevant model changes. Cache accepted decisions rather than silently reusing unreviewed proposals across projects. No persistent decision cache was introduced in this patch. |
| P2 efficiency | Snapshot/index geometry and identity within a generation | Reduce `Identity.Index`, `NextGroup`, room/ceiling/slab bounding-box reads and repeated dimension/resource work. Invalidate or update indexes on committed mutations; never reuse stale Revit elements after rollback. Measure collector/regeneration counts on large models. |
| P2 efficiency | Reduce regenerations after profiling | `SheetComposer.Compose` regenerates after each provisional placement. Investigate placing compatible items first and regenerating once before measurement; confirm viewport labels, text and schedules remain accurate. Do not remove necessary regeneration based only on source inspection. |
| P2 coverage | Independent room classification for carnet-only runs | `AnalyzeRooms` skips AI inputs when finishes are disabled, even if carnet selection depends on classifying unknown rooms. Add a classification-only contract and apply the result without proposing finish changes. |
| P2 geometry | Stronger representative-carnet grouping | Same name and rounded area do not prove identical layout. Include room boundary shape, openings, finish set and relevant level/phase information, or require explicit selection of representative rooms. |
| P2 UX | End-to-end progress and cancellation outcomes | Display requests completed/in flight, repair status, elapsed wall time and partial-production status. Recheck cancellation after UI pumping and before starting a new transaction. Dispose progress-window cancellation sources. Preserve Revit API entry-point rules if moving to modeless UI/ExternalEvent. |
| P2 robustness | Data validation and output limits | Validate catalogue duplicate IDs, profile resources, numeric ranges and safe filenames at load; impose request/context and response byte limits for unusually large custom catalogues/provider replies. Use project-scoped privacy options for client/address context. |
| P2 release quality | Host integration fixtures and CI | Keep Core CI automatic; run Revit adapter builds and smoke fixtures on a licensed Windows runner. Verify private/public provider failure behavior and maintain a small live API canary only with explicit cost limits. |

## Release smoke checklist

1. On a disposable model copy, run `Diagnostics.SmokeTest` and `Diagnostics.AutopilotTest` (simulated AI), then exercise the real UI. Inspect transaction rollbacks and reports.
2. Put two identically named rooms on different levels with different existing finishes. Verify independent proposals and preserved manual values. Repeat with unsaved grid edits.
3. Include duplicate joinery marks across families; confirm no advice is assigned to the wrong type.
4. Change selected production steps in the launch dialog; verify the actual plan matches them.
5. Reject an AI family/finish/prescription; verify rejected work is not applied and review status is logged. Check dependent library choices manually until the full dependency model is implemented.
6. Cancel during consultation and between production items. Confirm pending AI work stops, applied work is clearly understood, and Undo restores the Revit model. File exports are external artifacts and are not undone by Revit.
7. Rerun generation after manual edits; verify no duplicates and no unintended overwrites. Export the finish grid and compare it with actual room parameters.

## Primary references

- Autodesk: [Revit API deployment options — single-threaded API access](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API/files/Revit_API_Developers_Guide/Introduction/Getting_Started/Using_the_Autodesk_Revit_API/Revit_API_Revit_API_Developers_Guide_Introduction_Getting_Started_Using_the_Autodesk_Revit_API_Deployment_Options_html.html).
- OpenRouter: [Structured outputs](https://openrouter.ai/docs/guides/features/structured-outputs). Schema support is endpoint-dependent; local validation remains necessary.
- OpenRouter: [Errors and debugging](https://openrouter.ai/docs/api/reference/errors-and-debugging). Handle HTTP errors, response-body errors and retry guidance separately.
