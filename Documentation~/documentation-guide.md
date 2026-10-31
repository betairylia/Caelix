# Write and maintain Caelix documentation

Help the reader finish one task or understand one contract. Lead with the answer,
keep the page short, and link details where they are owned.

## Where things belong

| Location | Purpose |
|---|---|
| `README.md` | Package purpose and a link to getting started. |
| `Documentation~/index.md` | Tasks and their pages. |
| `manual/` | Prerequisites, steps, a working example, and expected result. |
| `reference/` | API choices, settings, and exact field meanings. |
| `internals/` | Ownership, coordinates, execution order, and lifetime rules. |
| `archive/` | Dated proposals and validation evidence. |
| `AGENTS.md` | Contributor rules and required reading. |

Core owns storage, traversal, serialization, and tick/message primitives. Caelix
owns world orchestration, integration, rendering, and authoring. Keep one owning
page per subject. Core links to this guide.

## Write for a reader in a hurry

- Put the useful answer before background. Define unfamiliar terms once.
- Give each section one purpose. Use numbered steps for procedures and tables
  when readers must choose between APIs or settings.
- Keep restrictions beside the operation: coordinates, ownership, permitted
  writes, and when borrowed data expires.
- Use real API and Inspector names. Link implementation and relevant tests.
- Keep examples small, with imports, prerequisites, and disposal. Label fragments
  and explain what the caller supplies. Never invent an API to shorten an example.
- Remove repeated explanations and migration history from introductions. Link
  the owning reference or archive instead.
- Keep public member details in C# XML comments; use pages to explain workflows.

## Update with the code

1. Check the implementation and callers before changing a documented contract.
2. Update its owning page, examples, navigation, and figures together.
3. Verify file links, anchors, symbols, and package requirements. Compile changed
   runnable examples when dependencies are available.
4. State what was actually checked. Source review, compilation, tests, and Play
   Mode are separate evidence. Prose edits do not need large benchmarks.

For implementation-sensitive sections, record the reviewed revision and date:

```text
Source reviewed: Caelix <commit>, Core <commit>, YYYY-MM-DD.
Validation: example compiled; not executed in Unity.
```

Only refresh that record for behavior you inspected. Keep original dates and
results in historical reports; add a status or successor link when needed.

## Links and figures

Use relative links within a repository and GitHub URLs between repositories.
Normal cross-repository links target `main`; note when pages await merging.
Use commit URLs for revision-specific evidence. Keep a short forwarding page
when moving a published document, and update navigation.

Use Mermaid for small flows and editable SVG for spatial diagrams. Include useful
alt text and explain essential rules in prose. Render changed figures and check
labels, arrows, contrast, and clipping. Interactive figures also need a static
overview, opening instructions, accessible controls, and tested reset behavior.

## Related pages

- [Caelix documentation](index.md)
- [Core documentation](https://github.com/betairylia/Caelix-Core/blob/main/Documentation~/index.md)
- [Historical documents](archive/index.md)
