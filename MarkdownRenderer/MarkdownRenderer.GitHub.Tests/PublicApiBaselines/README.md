# Public API baselines

These files freeze the consumer-visible metadata emitted by the built MarkdownRenderer package assemblies. The convenience and `All` meta-packages have no assembly of their own, so their referenced assemblies are covered instead.

The gate records public and protected types and members, including nullability, generic variance and constraints, parameter names and defaults, numeric enum values, virtual/abstract/override state, init-only accessors, struct layout, and compatibility-critical attributes. It also rejects public signatures that expose third-party implementation types, Win2D lifetime objects, or renderer-internal namespaces.

Normal test runs only compare against the checked-in files and fail for additions, removals, or signature changes. To accept an intentional change, run:

```powershell
.\eng\Update-PublicApiBaselines.ps1 -Update
```

Review every generated diff as a public compatibility decision before committing it. The update script rebuilds and reruns the immutable comparison after generation; it does not disable the default gate.
