# Architecture

## Source of truth
- `apps/testapp` contains the ShapeRuntime example application (API app).
- `packages/shaperuntime` contains the shared runtime source package, presented in the docs as ShapeRuntime.
- `web/` (monorepo root) contains the presentation app.
- `config/{app}` contains per-app runtime configuration in deploy output.

## Backend routing
- `apps/testapp/public/api.php` and `apps/pd/public/api.php` are front controllers.
- Shared route dispatch lives in `packages/shaperuntime/src/ShapeRuntime/Http/AppRouter.php`.
- AppRouter loads `app.php` and then merges `env.php` from the same config directory.
- API contract is `POST /api.php` with `{ "op": "...", "payload": { ... } }`.
- No web-server rewrite rules are required.

## Support folders
- `docs/shaperuntime` contains ShapeRuntime documentation.
- `docs/shape` contains Shape framework documentation.
- `apps/testapp/database` contains testapp schema and seed data.
- `apps/pd/database` is reserved for Product Discovery database assets.
- `docs/shaperuntime/example-query.json` contains a sample query payload.
- `bruno/shaperuntime` contains runtime API tests.
- `bruno/shape` contains Shape API tests.

## Generated output
- `.deploy` is not source code.

## Deploy shape
The generated package contains:
- `.deploy/testapp` - API public files
- `.deploy/www` - presentation site files
- `.deploy/src` or `.deploy/lib/shaperuntime.phar` - shared PHP source or PHAR package
- `.deploy/config` - private per-app config folders

## Why this split exists
The source layout is optimized for development. The deploy layout is optimized for hosting. Packaging bridges the two without forcing the repository to mirror the server filesystem.
