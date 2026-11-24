# Shape

Shape is a database-first API framework. This is a **polyglot monorepo** — the same
framework, implemented in two languages, sharing one specification and one website.

## Layout

| Path       | What it is |
|------------|------------|
| `php/`     | PHP implementation — `shape` (generators, router, MCP) on top of `shaperuntime` (SQL gateway, auth, batch executor), plus example apps. |
| `dotnet/`  | C# / .NET implementation — `src/Shape`, tests, and example apps. |
| `web/`     | The presentation & docs website (polyglot marketing site). |
| `docs/`    | Shared, language-neutral specification and architecture docs (`docs/shape`, `docs/shaperuntime`). |

## Getting started

- **PHP:** see [`php/README.md`](php/README.md)
- **C#:** see [`dotnet/README.md`](dotnet/README.md)
- **Spec & architecture:** see [`docs/shape/SPEC.md`](docs/shape/SPEC.md)

## Example consumer apps

Application-specific backends that *use* Shape live **outside** this repo and are wired in via symlink where needed
(e.g. a local `php/apps/<name>` symlink to a sibling checkout). The `apps/` folders inside `php/` and `dotnet/`
contain generic, in-repo example apps only.
