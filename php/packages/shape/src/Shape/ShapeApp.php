<?php

declare(strict_types=1);

namespace Shape;

use ShapeRuntime\Core\ApiException;
use ShapeRuntime\Core\CommandContext;
use ShapeRuntime\Core\Database;
use ShapeRuntime\Core\SqlDialect;
use Shape\Metadata\EntityReader;
use Shape\Metadata\EntityMeta;
use Shape\Metadata\FieldMeta;
use Shape\Metadata\DatabaseIntrospector;
use Shape\Generator\AdminMaskGenerator;
use Shape\Generator\MaskGenerator;
use Shape\Generator\ValidatorGenerator;

final class ShapeApp
{
    /**
     * Tables hardcoded as "secret" — never auto-introspected for CRUD, even
     * if listed in `access`. Reachable only through hand-written masks (or
     * the opt-in `admin_masks` built-ins, which strip sensitive columns).
     *
     * `audit_log` joins the baseline because it's an append-only forensic
     * trail; allowing arbitrary CRUD against it would defeat its purpose.
     * Read-only admin masks for audit_log are generated automatically when
     * the table is present (see _shape.audit_log_table detection below).
     */
    private const SECRET_TABLES_BASELINE = ['users', 'refresh_tokens', 'audit_log'];

    /**
     * Build config from PHP entity classes (code-first).
     *
     * @param string $entitiesDir Path to entity class files
     * @param string $configPath  Path to manual app.php config
     * @return array Complete config array ready for ShapeRouter
     */
    public static function fromEntities(string $entitiesDir, string $configPath): array
    {
        $config = self::loadConfig($configPath);
        $driver = self::detectDriver($config['db'] ?? []);
        $autoValidators = (bool)($config['auto_validators'] ?? true);

        // Try cache (invalidated on config/entity file changes)
        $sources = array_merge([$configPath], self::configEnvFiles($configPath), self::entityFiles($entitiesDir));
        $entities = self::loadEntitiesFromCache($config, $configPath, 'entities', $sources);

        if ($entities === null) {
            $reader = new EntityReader($autoValidators);
            $reader->scanDirectory($entitiesDir);
            $entities = $reader->getEntities();
            self::saveEntitiesToCache($config, $configPath, 'entities', $entities, $sources);
        }

        return self::buildFromEntities($config, $entities, $driver);
    }

    /**
     * Build config from live database schema (DB-first).
     * No entity files needed — the database IS the schema.
     *
     * @param string $configPath Path to app.php config (access, validate, commands)
     * @return array Complete config array ready for ShapeRouter
     */
    /**
     * Auto-discovering one-line entry point. Given the project directory,
     * looks for the highest-affinity config and dispatches to the correct
     * loader, then serves the request.
     *
     *     // public/api.php — the only file you write
     *     <?php
     *     require __DIR__ . '/../vendor/autoload.php';
     *     Shape\ShapeApp::run(__DIR__ . '/..');
     *
     * Discovery order:
     *
     *   1. `<projectDir>/config/database.php`  → fromDatabase()
     *   2. `<projectDir>/.env`                  → quickStart()
     *   3. nothing                              → quickStart() with empty .env
     *                                              (auto-SQLite kicks in)
     *
     * `env.local.php` / `env.php` are auto-merged into config/database.php
     * via the existing loadConfig() pipeline — no extra wiring needed.
     *
     * Service name is auto-detected from `composer.json` (the `name` field's
     * package portion) or the project directory's basename, with a final
     * fallback to `'shape'`. Override by passing `$serviceName` explicitly.
     *
     * Entity-first apps (`fromEntities`) are not auto-discovered: that mode
     * requires an explicit `entitiesDir` argument that has no canonical
     * project-relative location, so callers using that mode keep using
     * `ShapeApp::fromEntities()` + `ShapeRouter::run()` directly.
     *
     * @param string $projectDir  Absolute path to the project root
     *                              (the dir that contains config/, .env, etc.)
     * @param string $serviceName  Optional service-name override
     */
    public static function run(string $projectDir, string $serviceName = ''): void
    {
        $projectDir = rtrim($projectDir, '/');
        if ($serviceName === '') {
            $serviceName = self::detectServiceName($projectDir);
        }

        $dbConfigPath  = $projectDir . '/config/database.php';
        $envPath       = $projectDir . '/.env';

        if (is_file($dbConfigPath)) {
            $config = self::fromDatabase($dbConfigPath);
            ShapeRouter::run($config, $serviceName);
            return;
        }

        // Fall through to quickStart with the project's .env (or, when the
        // file doesn't exist, an empty .env — quickStart provisions an
        // auto-SQLite at <projectDir>/.shape/data.sqlite in that case).
        self::quickStart($envPath, $serviceName)->serve();
    }

    /**
     * Read `composer.json#name` (taking the package part of `vendor/package`),
     * fall back to the project directory's basename, with `'shape'` as the
     * final fallback. Used by run() when no explicit service name is passed.
     */
    private static function detectServiceName(string $projectDir): string
    {
        $composerPath = $projectDir . '/composer.json';
        if (is_file($composerPath)) {
            $raw = @file_get_contents($composerPath);
            if (is_string($raw)) {
                $json = @json_decode($raw, true);
                if (is_array($json) && isset($json['name']) && is_string($json['name'])) {
                    $name = $json['name'];
                    $slash = strrpos($name, '/');
                    $tail = $slash !== false ? substr($name, $slash + 1) : $name;
                    if ($tail !== '') {
                        return $tail;
                    }
                }
            }
        }
        $base = basename($projectDir);
        return $base !== '' ? $base : 'shape';
    }

    public static function fromDatabase(string $configPath): array
    {
        $config = self::loadConfig($configPath);
        $sources = array_merge([$configPath], self::configEnvFiles($configPath));
        return self::buildDatabaseConfig($config, $configPath, $sources);
    }

    /**
     * Single-line bootstrap: load `.env`, apply sensible defaults, introspect
     * the database, and return a runnable wrapper.
     *
     *     // public/api.php
     *     <?php
     *     require __DIR__ . '/../bootstrap.php';
     *     Shape\ShapeApp::quickStart(__DIR__ . '/../.env')->serve();
     *
     * Defaults applied unless overridden by `.env`:
     *   access         = ['*' => ['admin', 'user']]   (every introspected table)
     *   admin_masks    = true                          (built-in safe masks for users/refresh_tokens)
     *   auto_validators = true                         (notEmpty + maxLen from schema)
     *   cache          = (APP_ENV === 'prod')          (mtime-invalidated entity cache)
     *
     * Recognised .env keys:
     *   DB_DSN, DB_USER, DB_PASS                       — DB connection
     *   JWT_SECRET                                     — switches auth driver to 'jwt'
     *   JWT_TTL, JWT_REFRESH_TTL                       — token lifetimes (seconds)
     *   APP_NAME                                       — service name (default 'shape')
     *   APP_ENV                                        — 'prod' enables cache
     */
    public static function quickStart(string $envPath = '.env', string $serviceNameOverride = ''): ShapeQuickStart
    {
        $env = self::parseEnvFile($envPath);
        $config = self::buildConfigFromEnv($env, $envPath);
        $sources = is_file($envPath) ? [$envPath] : [];
        $config = self::buildDatabaseConfig($config, $envPath, $sources);
        $serviceName = $serviceNameOverride !== ''
            ? $serviceNameOverride
            : trim((string)($env['APP_NAME'] ?? 'shape'));
        return new ShapeQuickStart($config, $serviceName !== '' ? $serviceName : 'shape');
    }

    /**
     * Shared body for fromDatabase() and quickStart(). Given a config array
     * and a cache-key path (any path that uniquely identifies the app and
     * whose mtime invalidates the cache), runs introspection, generates the
     * mask surface, and merges in admin_masks if opted-in.
     *
     * @param array<string, mixed> $config
     * @param string $cacheKeyPath Path used as cache key + mtime invalidation source
     * @param list<string> $sources Additional files whose mtime invalidates the cache
     */
    private static function buildDatabaseConfig(array $config, string $cacheKeyPath, array $sources): array
    {
        $secretTables = self::resolveSecretTables($config);
        $driver = self::detectDriver($config['db'] ?? []);

        // Auth shorthand: `'auth' => 'jwt'` (or `=> true`) expands to the
        // full ['driver' => 'jwt'] block, with jwt_secret auto-generated by
        // the resolveAutoJwtSecret() path below. Lets a one-line auth config replace the four-line minimum.
        $authShorthand = $config['auth'] ?? null;
        if ($authShorthand === 'jwt' || $authShorthand === true) {
            $config['auth'] = ['driver' => 'jwt'];
        }

        // Normalize access shorthand (role-first → entity-first) before any
        // downstream consumer reads `$config['access']`.
        // ['admin' => ['t1', 't2']] is much shorter than
        // ['t1' => ['admin'], 't2' => ['admin']] for the common multi-table-
        // single-role case.
        if (is_array($config['access'] ?? null)) {
            $config['access'] = self::normalizeAccessConfig($config['access']);
        }

        // Auto-generate jwt_secret on first boot if the operator hasn't set
        // one — written to <project>/.shape/jwt-secret with mode 0600 and
        // re-read on subsequent boots. New apps drop one more line of
        // config; existing apps with explicit secrets are unaffected.
        if (($config['auth']['driver'] ?? 'none') === 'jwt'
            && empty($config['auth']['jwt_secret'])
        ) {
            $config['auth']['jwt_secret'] = self::resolveAutoJwtSecret($cacheKeyPath);
        }

        // Cache holds a *bundle*: entities + admin masks + audit-log detection
        // result. Earlier versions cached only entities, so warm requests still
        // ran listTables()/describeColumns() for admin_masks + audit_log
        // detection — two extra round trips per request against a remote DB.
        // Bundling those into the cache cuts warm boots from ~1.4s to ~5ms on
        // remote PostgreSQL. Cache invalidates on
        // source-file mtime only; DB schema changes still need clearCache().
        $bundle = self::loadDatabaseBundle($config, $cacheKeyPath, $sources);

        if ($bundle === null) {
            $database = new Database($config['db'] ?? []);
            $introspector = new DatabaseIntrospector($database);

            $accessConfig = $config['access'] ?? [];
            $validateConfig = $config['validate'] ?? [];
            $excludeTables = array_values(array_unique(array_merge(
                $config['exclude_tables'] ?? [],
                $secretTables,
            )));
            $allowlist = self::resolveAllowlist($config, $secretTables);
            $autoValidators = (bool)($config['auto_validators'] ?? true);

            $entities = $introspector->introspect($accessConfig, $validateConfig, $excludeTables, $allowlist, $autoValidators);

            $adminMasks = ($config['admin_masks'] ?? false)
                ? AdminMaskGenerator::generate($introspector, $secretTables, self::normalizeTenancy($config['tenancy'] ?? null))
                : [];

            // Audit-log Tier 1: detect the conventional `audit_log` table. When
            // present, BatchExecutor writes one row per mutation; built-in
            // admin-only masks (adminList / byActor / byTarget / byTable) get
            // generated alongside. Opt out with 'audit' => false in config.
            $auditLogTable = null;
            $auditMasks = [];
            $allTables = null;
            if (($config['audit'] ?? true) !== false) {
                $allTables = $introspector->listTables();
                if (in_array('audit_log', $allTables, true)) {
                    $auditLogTable = 'audit_log';
                    $auditMasks = self::buildAuditLogMasks($driver);
                }
            }

            // Detect OIDC-only users schemas at cold-boot time so a stale
            // mismatch (auth.driver=jwt but users.password_hash absent — common
            // when migrating to Entra ID / Auth0 and forgetting the local-JWT
            // pathway is dead) surfaces as a warning at boot, not a 500 on
            // first auth.token request. Re-checked from the bundle on every
            // warm boot so the warning persists without re-introspection.
            //
            // While we have the `users` columns in hand, also auto-detect the
            // best `users_lookup_column` for the OIDC verifier — saves the
            // operator one config line in the common case (oid / sub /
            // azure_oid / auth0_id / external_id). Ambiguous schemas surface
            // a warning rather than picking silently.
            $usersLacksPasswordHash = false;
            $autoUsersLookupColumn = null;
            if (($config['auth']['driver'] ?? 'none') === 'jwt') {
                $allTables ??= $introspector->listTables();
                if (in_array('users', $allTables, true)) {
                    $userCols = $introspector->describeColumns('users');
                    $colNames = array_map(static fn(array $c) => (string)$c['name'], $userCols);
                    $usersLacksPasswordHash = !in_array('password_hash', $colNames, true);

                    // Tenancy requires users to carry the discriminator column
                    // (MULTI-TENANCY-PLAN §2.2) — fail loud at boot, never guess.
                    $tenancyCheck = self::normalizeTenancy($config['tenancy'] ?? null);
                    if ($tenancyCheck !== null && !in_array($tenancyCheck['column'], $colNames, true)) {
                        throw new ApiException(
                            'CONFIG_INVALID',
                            "Tenancy is enabled but the users table has no '{$tenancyCheck['column']}' column.",
                            500,
                            null,
                            ['hint' => "Add '{$tenancyCheck['column']}' to the users table so each account belongs to a tenant, then clear the Shape cache."]
                        );
                    }

                    if (is_array($config['auth']['oidc'] ?? null)
                        && empty($config['auth']['oidc']['users_lookup_column'])
                    ) {
                        $candidates = ['oid', 'sub', 'azure_oid', 'auth0_id', 'external_id'];
                        $found = array_values(array_intersect($candidates, $colNames));
                        if (count($found) === 1) {
                            $autoUsersLookupColumn = $found[0];
                        } elseif (count($found) > 1) {
                            self::warnOnce(
                                '[Shape] Multiple candidate user-lookup columns found in `users`: '
                                . implode(', ', $found)
                                . '. Set auth.oidc.users_lookup_column explicitly to disambiguate.'
                            );
                        }
                    }
                }
            }

            $bundle = [
                'entities' => $entities,
                'admin_masks' => $adminMasks,
                'audit_log_table' => $auditLogTable,
                'audit_masks' => $auditMasks,
                'users_lacks_password_hash' => $usersLacksPasswordHash,
                'auto_users_lookup_column' => $autoUsersLookupColumn,
            ];
            self::saveDatabaseBundle($config, $cacheKeyPath, $sources, $bundle);
        }

        $config = self::buildFromEntities($config, $bundle['entities'], $driver);

        if (!empty($bundle['admin_masks'])) {
            // Manual masks (config['masks']) win over admin masks.
            $config['masks'] = array_merge($bundle['admin_masks'], $config['masks'] ?? []);
        }

        if (($bundle['audit_log_table'] ?? null) !== null) {
            $config['_shape']['audit_log_table'] = $bundle['audit_log_table'];
            if (!empty($bundle['audit_masks'])) {
                $config['masks'] = array_merge($bundle['audit_masks'], $config['masks'] ?? []);
            }
        }

        // Re-validate after the admin / audit mask merge — those built-ins
        // join the surface here, past buildFromEntities' contract sweep.
        self::validateMaskContracts(
            $config['masks'] ?? [],
            is_array($config['tenancy'] ?? null) ? $config['tenancy'] : null,
            is_array($config['_shape']['tenant_scopes'] ?? null) ? $config['_shape']['tenant_scopes'] : [],
            is_array($config['scopes'] ?? null) ? $config['scopes'] : [],
            is_array($config['_shape']['row_scopes'] ?? null) ? $config['_shape']['row_scopes'] : []
        );
        self::validateStrictSecurity($config);

        // Apply the auto-detected users_lookup_column when the operator
        // hasn't pinned one explicitly. Survives warm boots via the bundle.
        if (is_array($config['auth']['oidc'] ?? null)
            && empty($config['auth']['oidc']['users_lookup_column'])
            && is_string($bundle['auto_users_lookup_column'] ?? null)
        ) {
            $config['auth']['oidc']['users_lookup_column'] = $bundle['auto_users_lookup_column'];
        }

        if (($bundle['users_lacks_password_hash'] ?? false) === true
            && ($config['auth']['driver'] ?? 'none') === 'jwt'
        ) {
            // Surface the fact to LoginService so auth.token / changePassword
            // short-circuit with AUTH_NOT_CONFIGURED instead of running a
            // SELECT that errors out as DB_ERROR ("column does not exist").
            // The dev can override by setting auth.local_login_disabled =>
            // false explicitly. This part of the fix runs whether or not
            // OIDC is configured — the SQL would still fail.
            if (!array_key_exists('local_login_disabled', $config['auth'] ?? [])) {
                $config['auth']['local_login_disabled'] = true;
            }

            // Diagnostic warning is silenced when OIDC is configured — that's
            // the documented coexistence pattern: local-login disabled, OIDC
            // is the intended path. Logging "configure auth.oidc" when the
            // operator has already done so was the original noise complaint.
            $oidc = $config['auth']['oidc'] ?? null;
            $oidcConfigured = is_array($oidc) && $oidc !== [];
            if (!$oidcConfigured) {
                self::warnOnce(
                    '[Shape] auth.driver=jwt but users.password_hash column is absent. ' .
                    'Local-JWT login will fail; if you intend OIDC-only auth, configure ' .
                    "auth.oidc and remove auth.driver=jwt or accept the local-login dead end."
                );
            }
        }

        return $config;
    }

    /** Emit a Shape warning to error_log at most once per process per message. */
    private static function warnOnce(string $message): void
    {
        static $seen = [];
        if (isset($seen[$message])) {
            return;
        }
        $seen[$message] = true;
        @error_log($message);
    }

    /**
     * Built-in admin-only read masks for the audit_log table when it's
     * present. Manual masks of the same name in app.php still win (override).
     *
     * @return array<string, array<string, mixed>>
     */
    private static function buildAuditLogMasks(string $driver): array
    {
        $q = static fn(string $name): string => SqlDialect::quoteIdentifier($driver, $name);
        $cols = $q('id') . ', ' . $q('occurred_at') . ', ' . $q('actor_id') . ', '
              . $q('actor_email') . ', ' . $q('actor_role') . ', ' . $q('table_name') . ', '
              . $q('row_id') . ', ' . $q('action') . ', ' . $q('changed');
        $tbl = $q('audit_log');

        return [
            'audit_log.adminList' => [
                'sql' => $driver === 'sqlsrv'
                    ? "SELECT TOP 200 {$cols} FROM {$tbl} ORDER BY {$q('occurred_at')} DESC"
                    : "SELECT {$cols} FROM {$tbl} ORDER BY {$q('occurred_at')} DESC LIMIT 200",
                'params' => [],
                'roles' => ['admin'],
                'description' => '(admin only) List the most recent 200 audit-log entries, newest first.',
            ],
            'audit_log.byActor' => [
                'sql' => "SELECT {$cols} FROM {$tbl} WHERE {$q('actor_id')} = :actor_id ORDER BY {$q('occurred_at')} DESC",
                'params' => ['actor_id' => ['type' => 'string', 'required' => true]],
                'roles' => ['admin'],
                'description' => '(admin only) All audit entries for a specific actor.',
            ],
            'audit_log.byTable' => [
                'sql' => "SELECT {$cols} FROM {$tbl} WHERE {$q('table_name')} = :table_name ORDER BY {$q('occurred_at')} DESC",
                'params' => ['table_name' => ['type' => 'string', 'required' => true]],
                'roles' => ['admin'],
                'description' => '(admin only) All audit entries against one table.',
            ],
            'audit_log.byTarget' => [
                'sql' => "SELECT {$cols} FROM {$tbl} WHERE {$q('table_name')} = :table_name AND {$q('row_id')} = :row_id ORDER BY {$q('occurred_at')} DESC",
                'params' => [
                    'table_name' => ['type' => 'string', 'required' => true],
                    'row_id'     => ['type' => 'string', 'required' => true],
                ],
                'roles' => ['admin'],
                'description' => '(admin only) Full mutation history of one row in one table.',
            ],
        ];
    }

    /**
     * Provision an auto-SQLite file alongside the .env when no DB_DSN is set.
     *
     * The file lives at `<dirname(envPath)>/.shape/data.sqlite`, anchored to
     * the project (the .env's directory) rather than the current working
     * directory — so the same project boots the same DB regardless of where
     * `php` is invoked from.
     *
     * The directory is created with mkdir -p semantics; failure is silent
     * because PDO will surface a clearer "unable to open database" error
     * than a mkdir warning would.
     */
    private static function autoSqliteDsn(string $envPath): string
    {
        $base = dirname($envPath);
        if ($base === '' || $base === '.') {
            $base = getcwd() ?: '.';
        }
        $dir = $base . '/.shape';
        if (!is_dir($dir)) {
            @mkdir($dir, 0777, true);
        }
        return 'sqlite:' . $dir . '/data.sqlite';
    }

    /**
     * Tiny self-contained .env parser (no dependency on phpdotenv).
     * Skips blank lines and `#` comments. Strips matched single/double quotes.
     * Missing file returns []; keys are not exported to $_ENV / getenv().
     *
     * @return array<string, string>
     */
    private static function parseEnvFile(string $path): array
    {
        if (!is_file($path)) {
            return [];
        }
        $lines = @file($path, FILE_IGNORE_NEW_LINES | FILE_SKIP_EMPTY_LINES);
        if ($lines === false) {
            return [];
        }
        $env = [];
        foreach ($lines as $line) {
            $trimmed = trim($line);
            if ($trimmed === '' || str_starts_with($trimmed, '#')) {
                continue;
            }
            $eq = strpos($trimmed, '=');
            if ($eq === false) {
                continue;
            }
            $key = trim(substr($trimmed, 0, $eq));
            if ($key === '' || !preg_match('/^[A-Za-z_][A-Za-z0-9_]*$/', $key)) {
                continue;
            }
            $value = trim(substr($trimmed, $eq + 1));
            // Strip a single matched pair of surrounding quotes, but only that pair.
            $len = strlen($value);
            if ($len >= 2) {
                $first = $value[0];
                $last = $value[$len - 1];
                if (($first === '"' && $last === '"') || ($first === "'" && $last === "'")) {
                    $value = substr($value, 1, -1);
                }
            }
            $env[$key] = $value;
        }
        return $env;
    }

    /**
     * Translate a parsed .env map into a Shape config array with sensible
     * production defaults. Only the keys documented under quickStart() are
     * recognised; unknown keys are ignored (so devs can use the same .env
     * for app-specific config).
     *
     * If `DB_DSN` is missing or empty, an auto-SQLite file is provisioned
     * at `<dirname(envPath)>/.shape/data.sqlite` and the directory is
     * created on demand. This makes an empty .env (or no .env at all) a
     * working dev backend — zero database configuration for prototyping.
     *
     * @param array<string, string> $env
     * @return array<string, mixed>
     */
    private static function buildConfigFromEnv(array $env, string $envPath = '.env'): array
    {
        $db = [];
        $dsn = isset($env['DB_DSN']) ? trim((string)$env['DB_DSN']) : '';
        if ($dsn === '') {
            $dsn = self::autoSqliteDsn($envPath);
        }
        $db['dsn'] = $dsn;
        if (isset($env['DB_USER'])) {
            $db['user'] = $env['DB_USER'];
        }
        if (isset($env['DB_PASS'])) {
            $db['pass'] = $env['DB_PASS'];
        }
        if (isset($env['DB_SCHEMA'])) {
            $schema = trim((string)$env['DB_SCHEMA']);
            if ($schema !== '') {
                $db['schema'] = $schema;
            }
        }

        $auth = ['driver' => 'none'];
        if (isset($env['JWT_SECRET']) && $env['JWT_SECRET'] !== '') {
            $auth = [
                'driver' => 'jwt',
                'jwt_secret' => $env['JWT_SECRET'],
            ];
            if (isset($env['JWT_TTL']) && ctype_digit($env['JWT_TTL'])) {
                $auth['access_token_ttl'] = (int)$env['JWT_TTL'];
            }
            if (isset($env['JWT_REFRESH_TTL']) && ctype_digit($env['JWT_REFRESH_TTL'])) {
                $auth['refresh_token_ttl'] = (int)$env['JWT_REFRESH_TTL'];
            }
        }

        return [
            'db' => $db,
            'auth' => $auth,
            'access' => ['*' => ['admin', 'user']],
            'admin_masks' => true,
            'auto_validators' => true,
            // Cache defaults to true in every environment. mtime invalidation
            // catches config / env edits automatically; schema changes need
            // an explicit ShapeApp::clearCache() call. Caching only in prod
            // was hostile to remote-DB development — cold introspection is
            // RTT-bound, so a developer hitting localhost against a remote
            // PG would wait tens of seconds per request without it.
            'cache' => true,
        ];
    }

    /**
     * Resolve which tables should be auto-introspected for CRUD.
     *
     *   include_tables set        → use it (minus secret tables)
     *   access['*'] set           → null = introspect everything (minus exclude+secret)
     *   access has specific keys  → those keys (minus '*' and secret tables)
     *   access empty              → empty list = introspect nothing (safe default)
     *
     * @param list<string> $secretTables
     * @return list<string>|null
     */
    /**
     * Read or generate-and-persist a JWT signing secret. The file lives at
     * `<project>/.shape/jwt-secret` (next to the auto-SQLite path) with
     * mode 0600. Generated values use 32 random bytes hex-encoded
     * (256 bits of entropy — comfortably above the HS256 minimum).
     *
     * `cacheKeyPath` is the path the caller used to identify the project:
     * the env path for `quickStart()`, the config-file path for
     * `fromDatabase()`. The secret is anchored to that path's directory,
     * so a project's secret survives across boots regardless of CWD.
     *
     * Operators who want to control rotation explicitly set
     * `auth.jwt_secret` directly and this code never runs.
     */
    private static function resolveAutoJwtSecret(string $cacheKeyPath): string
    {
        $base = dirname($cacheKeyPath);
        if ($base === '' || $base === '.') {
            $base = getcwd() ?: '.';
        }
        // Anchor under .shape/ so it sits next to the auto-SQLite file when
        // both auto-paths are in play. Mode 0700 on the dir, 0600 on the file.
        $dir = $base . '/.shape';
        $secretPath = $dir . '/jwt-secret';

        if (is_file($secretPath)) {
            $existing = @file_get_contents($secretPath);
            if (is_string($existing)) {
                $trimmed = trim($existing);
                if ($trimmed !== '') {
                    return $trimmed;
                }
            }
        }

        if (!is_dir($dir)) {
            @mkdir($dir, 0700, true);
        }
        $secret = bin2hex(random_bytes(32));
        if (@file_put_contents($secretPath, $secret, LOCK_EX) !== false) {
            @chmod($secretPath, 0600);
        }
        return $secret;
    }

    /**
     * Detect role-first `access` shorthand and rewrite to the canonical
     * entity-first form. Both forms can coexist in the same config; each
     * key is examined independently.
     *
     *     // Role-first (shorthand) — collapses N tables to 1 line per role
     *     'access' => ['admin' => ['locations', 'warehouses', 'commodities']]
     *
     *     // Entity-first (canonical) — what the rest of the framework reads
     *     'access' => [
     *         'locations'   => ['admin'],
     *         'warehouses'  => ['admin'],
     *         'commodities' => ['admin'],
     *     ]
     *
     * Heuristic: a key is treated as role-first when its value is a
     * non-associative list of strings AND the value items look like table
     * names rather than role names. The existing entity-first form has
     * values that are either lists of role strings (e.g. ['admin', 'user'])
     * or role-split objects (['read' => ['admin'], 'write' => ['user']]).
     *
     * To avoid false positives on the entity-first form, we use the
     * `auth.assignable_roles` set (default ['user', 'admin']) as the
     * known-role list. A top-level key matching a known role with a list
     * value gets inverted; everything else passes through unchanged.
     *
     * @param array<string, mixed> $access
     * @return array<string, mixed>
     */
    private static function normalizeAccessConfig(array $access): array
    {
        // Default known roles. The role-first shorthand is a developer
        // convenience for the common 'user' + 'admin' case; configs that
        // use exotic role names can still use the entity-first form, or
        // they can extend the recognized list via auth.assignable_roles
        // (which the rest of the framework already honors).
        $knownRoles = ['user', 'admin'];

        $normalized = [];
        foreach ($access as $key => $value) {
            if ($key === '*') {
                $normalized[$key] = $value;
                continue;
            }

            $isList = is_array($value) && (array_is_list($value));
            $isListOfStrings = $isList && array_filter($value, 'is_string') === array_values($value);

            if (is_string($key) && in_array($key, $knownRoles, true) && $isListOfStrings) {
                // Role-first: invert. Each item is a table name granted this role.
                foreach ($value as $tableName) {
                    if (!is_string($tableName) || $tableName === '') {
                        continue;
                    }
                    if (!isset($normalized[$tableName])) {
                        $normalized[$tableName] = [];
                    }
                    if (is_array($normalized[$tableName]) && !in_array($key, $normalized[$tableName], true)) {
                        $normalized[$tableName][] = $key;
                    }
                }
                continue;
            }

            // Entity-first or wildcard — pass through unchanged. If the
            // entry was already entity-first, merge any role-first
            // contributions added earlier in this same pass.
            if (isset($normalized[$key]) && is_array($normalized[$key]) && is_array($value)) {
                $normalized[$key] = array_values(array_unique(array_merge($normalized[$key], $value)));
            } else {
                $normalized[$key] = $value;
            }
        }

        return $normalized;
    }

    private static function resolveAllowlist(array $config, array $secretTables): ?array
    {
        $accessConfig = $config['access'] ?? [];
        $includeTables = $config['include_tables'] ?? null;

        if ($includeTables !== null) {
            return array_values(array_filter(
                $includeTables,
                fn(string $t) => !in_array($t, $secretTables, true),
            ));
        }

        if (isset($accessConfig['*'])) {
            return null; // SHOW TABLES, then exclude_tables filter applies
        }

        if ($accessConfig === []) {
            return [];
        }

        return array_values(array_filter(
            array_keys($accessConfig),
            fn(string $t) => $t !== '*' && !in_array($t, $secretTables, true),
        ));
    }

    /**
     * Combine the hardcoded secret-table baseline with optional `secret_tables` config.
     * Devs can extend the list, never shrink it.
     *
     * @return list<string>
     */
    private static function resolveSecretTables(array $config): array
    {
        $extra = $config['secret_tables'] ?? [];
        if (!is_array($extra)) {
            $extra = [];
        }
        return array_values(array_unique(array_merge(self::SECRET_TABLES_BASELINE, $extra)));
    }

    // ── Config loading ────────────────────────────────────────────────

    private static function loadConfig(string $configPath): array
    {
        $config = [];
        if (is_file($configPath)) {
            $loaded = require $configPath;
            if (is_array($loaded)) {
                $config = self::expandConfigShorthands($loaded);
            }
        }

        $envFile = self::activeEnvFile($configPath);
        if ($envFile !== null) {
            $env = require $envFile;
            if (is_array($env)) {
                $env = self::expandConfigShorthands($env);
                $config = array_replace_recursive($env, $config);
            }
        }

        $configDir = self::configDirectory($configPath);
        $config = self::resolveMaskSqlFiles($config, $configDir);

        return $config;
    }

    /**
     * Resolve `<mask>.sqlFile` entries into inline `sql` bodies.
     *
     * @param array<string, mixed> $config
     * @return array<string, mixed>
     */
    private static function resolveMaskSqlFiles(array $config, string $configDir): array
    {
        if (!is_array($config['masks'] ?? null)) {
            return $config;
        }

        $resolvedMasks = [];
        foreach ($config['masks'] as $maskName => $mask) {
            if (!is_string($maskName) || !is_array($mask)) {
                $resolvedMasks[$maskName] = $mask;
                continue;
            }
            $resolvedMasks[$maskName] = self::resolveMaskSqlFile($maskName, $mask, $configDir);
        }

        $config['masks'] = $resolvedMasks;
        return $config;
    }

    /**
     * @param array<string, mixed> $mask
     * @return array<string, mixed>
     */
    private static function resolveMaskSqlFile(string $maskName, array $mask, string $configDir): array
    {
        $sql = isset($mask['sql']) && is_string($mask['sql']) ? $mask['sql'] : '';
        $sqlFile = isset($mask['sqlFile']) && is_string($mask['sqlFile']) ? $mask['sqlFile'] : '';
        $hasSql = preg_match('/\S/', $sql) === 1;
        $hasSqlFile = preg_match('/\S/', $sqlFile) === 1;

        if ($hasSql && $hasSqlFile) {
            throw new ApiException(
                'CONFIG_INVALID',
                "Mask '{$maskName}' specifies both `sql` and `sqlFile`.",
                500,
                null,
                [
                    'mask' => $maskName,
                    'hint' => 'Specify exactly one of `sql` (inline SQL) or `sqlFile` (path to a .sql file relative to the config directory).',
                ]
            );
        }

        if (!$hasSqlFile) {
            return $mask;
        }

        if (self::isAbsolutePath($sqlFile)) {
            throw new ApiException(
                'CONFIG_INVALID',
                "Mask '{$maskName}' has an absolute or home-relative `sqlFile` path: {$sqlFile}.",
                500,
                null,
                [
                    'mask' => $maskName,
                    'sqlFile' => $sqlFile,
                    'hint' => 'Use a path relative to the directory containing the config file.',
                ]
            );
        }

        $resolvedPath = $configDir . DIRECTORY_SEPARATOR . $sqlFile;
        if (!is_file($resolvedPath)) {
            throw new ApiException(
                'CONFIG_INVALID',
                "Mask '{$maskName}' references missing `sqlFile`: {$resolvedPath}.",
                500,
                null,
                [
                    'mask' => $maskName,
                    'sqlFile' => $sqlFile,
                    'resolvedPath' => $resolvedPath,
                    'hint' => 'Path is resolved relative to the directory containing the config file.',
                ]
            );
        }

        $body = file_get_contents($resolvedPath);
        if (!is_string($body)) {
            throw new ApiException(
                'CONFIG_INVALID',
                "Mask '{$maskName}' could not read `sqlFile`: {$resolvedPath}.",
                500,
                null,
                [
                    'mask' => $maskName,
                    'sqlFile' => $sqlFile,
                    'resolvedPath' => $resolvedPath,
                    'hint' => 'Ensure the file exists and is readable.',
                ]
            );
        }

        if (str_starts_with($body, "\xEF\xBB\xBF")) {
            $body = substr($body, 3);
        }

        $mask['sql'] = $body;
        unset($mask['sqlFile']);
        return $mask;
    }

    private static function configDirectory(string $configPath): string
    {
        $dir = dirname($configPath);
        if ($dir === '' || $dir === '.') {
            $dir = getcwd() ?: '.';
        }
        return $dir;
    }

    private static function isAbsolutePath(string $path): bool
    {
        if ($path === '') {
            return false;
        }

        if (str_starts_with($path, '~')) {
            return true;
        }

        if (PHP_OS_FAMILY === 'Windows') {
            return str_starts_with($path, '/')
                || str_starts_with($path, '\\')
                || preg_match('/^[A-Za-z]:/', $path) === 1;
        }

        return str_starts_with($path, '/');
    }

    /**
     * Expand string-form shorthands into their canonical array form before
     * merging across files. Without this step, a `'auth' => 'jwt'` line in
     * `config/database.php` would clobber a sibling `'auth' => ['oidc' =>
     * ...]` block in `config/env.local.php` because array_replace_recursive
     * can't deep-merge a string with an array.
     *
     * Currently expands:
     *   'auth' => 'jwt'  (or  'auth' => true)   →  ['driver' => 'jwt']
     *   'pk_assignment' => 'manual'              →  ['*' => 'manual']
     *
     * @param array<string, mixed> $config
     * @return array<string, mixed>
     */
    private static function expandConfigShorthands(array $config): array
    {
        if (($config['auth'] ?? null) === 'jwt' || ($config['auth'] ?? null) === true) {
            $config['auth'] = ['driver' => 'jwt'];
        }
        if (is_string($config['pk_assignment'] ?? null)) {
            $config['pk_assignment'] = ['*' => $config['pk_assignment']];
        }
        return $config;
    }

    /** @return list<string> */
    private static function configEnvFiles(string $configPath): array
    {
        $dir = dirname($configPath);
        $files = [];
        foreach (['env.local.php', 'env.php'] as $name) {
            $p = $dir . '/' . $name;
            if (is_file($p)) {
                $files[] = $p;
            }
        }
        return $files;
    }

    private static function activeEnvFile(string $configPath): ?string
    {
        $dir = dirname($configPath);
        $local = $dir . '/env.local.php';
        $env = $dir . '/env.php';
        if (is_file($local)) return $local;
        if (is_file($env)) return $env;
        return null;
    }

    /** @return list<string> */
    private static function entityFiles(string $entitiesDir): array
    {
        if (!is_dir($entitiesDir)) {
            return [];
        }
        $files = glob($entitiesDir . '/*.php');
        return is_array($files) ? $files : [];
    }

    // ── Cache handling ────────────────────────────────────────────────

    /**
     * Load cached entities if cache is enabled and valid.
     *
     * @param list<string> $sources Files whose mtimes invalidate the cache
     * @return array<string, EntityMeta>|null
     */
    private static function loadEntitiesFromCache(array $config, string $configPath, string $mode, array $sources): ?array
    {
        $cachePath = self::resolveCachePath($config, $configPath, $mode);
        if ($cachePath === null) {
            return null;
        }
        if (!is_file($cachePath)) {
            return null;
        }

        $cacheTime = filemtime($cachePath);
        foreach ($sources as $source) {
            if (is_file($source) && filemtime($source) > $cacheTime) {
                return null; // Source changed, cache invalid
            }
        }

        $raw = @file_get_contents($cachePath);
        if ($raw === false) {
            return null;
        }

        $entities = @unserialize($raw, ['allowed_classes' => [EntityMeta::class, FieldMeta::class]]);
        if (!is_array($entities)) {
            return null;
        }

        // Sanity check: all values must be EntityMeta
        foreach ($entities as $entity) {
            if (!$entity instanceof EntityMeta) {
                return null;
            }
        }

        return $entities;
    }

    /**
     * Save entities to cache file. Best-effort — silent on failure.
     *
     * @param array<string, EntityMeta> $entities
     * @param list<string> $sources
     */
    private static function saveEntitiesToCache(array $config, string $configPath, string $mode, array $entities, array $sources): void
    {
        $cachePath = self::resolveCachePath($config, $configPath, $mode);
        if ($cachePath === null) {
            return;
        }

        $dir = dirname($cachePath);
        if (!is_dir($dir)) {
            @mkdir($dir, 0777, true);
        }

        @file_put_contents($cachePath, serialize($entities), LOCK_EX);
    }

    /**
     * Load the database-mode cache bundle: entities + admin masks + audit-log
     * detection result. Returns null on cache miss, format mismatch, or any
     * source-file mtime newer than the cache.
     *
     * Earlier versions of this cache stored only the entity list; those legacy
     * payloads (no `v` key) are rejected here and re-introspected on the
     * next request. Clean, automatic upgrade.
     *
     * @param list<string> $sources
     * @return array{entities:array<string, EntityMeta>, admin_masks:array<string, array<string, mixed>>, audit_log_table:?string, audit_masks:array<string, array<string, mixed>>, users_lacks_password_hash:bool}|null
     */
    private static function loadDatabaseBundle(array $config, string $configPath, array $sources): ?array
    {
        $cachePath = self::resolveCachePath($config, $configPath, 'database');
        if ($cachePath === null || !is_file($cachePath)) {
            return null;
        }

        $cacheTime = filemtime($cachePath);
        foreach ($sources as $source) {
            if (is_file($source) && filemtime($source) > $cacheTime) {
                return null;
            }
        }

        $raw = @file_get_contents($cachePath);
        if ($raw === false) {
            return null;
        }

        $data = @unserialize($raw, ['allowed_classes' => [EntityMeta::class, FieldMeta::class]]);
        if (!is_array($data) || ($data['v'] ?? null) !== 2) {
            return null;
        }
        if (!isset($data['entities']) || !is_array($data['entities'])) {
            return null;
        }
        foreach ($data['entities'] as $entity) {
            if (!$entity instanceof EntityMeta) {
                return null;
            }
        }

        return [
            'entities'                  => $data['entities'],
            'admin_masks'               => is_array($data['admin_masks'] ?? null) ? $data['admin_masks'] : [],
            'audit_log_table'           => isset($data['audit_log_table']) && is_string($data['audit_log_table']) ? $data['audit_log_table'] : null,
            'audit_masks'               => is_array($data['audit_masks'] ?? null) ? $data['audit_masks'] : [],
            'users_lacks_password_hash' => (bool)($data['users_lacks_password_hash'] ?? false),
            'auto_users_lookup_column'  => isset($data['auto_users_lookup_column']) && is_string($data['auto_users_lookup_column'])
                ? $data['auto_users_lookup_column']
                : null,
        ];
    }

    /**
     * @param list<string> $sources
     * @param array{entities:array<string, EntityMeta>, admin_masks:array<string, array<string, mixed>>, audit_log_table:?string, audit_masks:array<string, array<string, mixed>>, users_lacks_password_hash:bool} $bundle
     */
    private static function saveDatabaseBundle(array $config, string $configPath, array $sources, array $bundle): void
    {
        $cachePath = self::resolveCachePath($config, $configPath, 'database');
        if ($cachePath === null) {
            return;
        }

        $dir = dirname($cachePath);
        if (!is_dir($dir)) {
            @mkdir($dir, 0777, true);
        }

        // v2: EntityMeta gained writeRolesByAction — older payloads must
        // re-introspect (their unserialized objects lack the property).
        @file_put_contents($cachePath, serialize(['v' => 2] + $bundle), LOCK_EX);
    }

    /**
     * Resolve the cache file path based on config.
     *
     *   'cache' => false (default) → null (no caching)
     *   'cache' => true            → sys_get_temp_dir()/shape_cache/{hash}.cache
     *   'cache' => '/custom/path'  → /custom/path/{hash}.cache
     */
    private static function resolveCachePath(array $config, string $configPath, string $mode): ?string
    {
        $cache = $config['cache'] ?? false;
        if ($cache === false || $cache === null) {
            return null;
        }

        $baseDir = is_string($cache)
            ? rtrim($cache, '/')
            : sys_get_temp_dir() . '/shape_cache';

        $key = hash('sha256', $configPath . '|' . $mode);
        return $baseDir . '/' . $key . '.cache';
    }

    // ── Build config from entities ────────────────────────────────────

    /**
     * Build complete config from entity metadata (shared by both modes).
     *
     * @param array<string, EntityMeta> $entities
     */
    private static function buildFromEntities(array $config, array $entities, string $driver = 'mysql'): array
    {
        // Default 100; explicit `'default_list_limit' => 0` opts out.
        // Auto-CRUD without a default limit OOMs on tables ≥10k rows.
        $defaultListLimit = array_key_exists('default_list_limit', $config)
            ? (int)$config['default_list_limit']
            : 100;

        // Manual-PK detection happens here — the one place where config and
        // introspected/declared schema meet — and travels to the runtime in
        // the writable masks (MANUAL-PK-PLAN §2.2/§2.3).
        $pkAssignment = self::normalizePkAssignment($config['pk_assignment'] ?? null);
        $pkStrategies = self::resolvePkStrategies($entities, $pkAssignment);

        // Multi-tenancy detection (MULTI-TENANCY-PLAN §2.2). Same seam as
        // manual-PK: config + schema meet here, and the scoping predicate
        // travels to the runtime baked into the generated masks. Null when
        // tenancy is not enabled.
        $tenancy = self::normalizeTenancy($config['tenancy'] ?? null);
        $tenantScopes = [];
        $unscopedRoles = [];
        if ($tenancy !== null) {
            // The strict "exposed table missing tenant column" check must only
            // sweep tables that are actually served (access-exposed), mirroring
            // the .NET TenancyResolver — otherwise an introspected-but-unexposed
            // table (e.g. brought in via include_tables, or an internal table
            // under a specific access map) falsely fails the boot. A wildcard or
            // empty access map means "everything introspected is exposed" → null.
            $accessMap = $config['access'] ?? [];
            $exposedTables = (is_array($accessMap) && $accessMap !== [] && !isset($accessMap['*']))
                ? array_values(array_filter(array_keys($accessMap), fn($t): bool => $t !== '*'))
                : null;
            $tenantScopes = self::resolveTenantScopes($entities, $tenancy, $exposedTables);
            $unscopedRoles = $tenancy['unscoped_roles'];
            $config['tenancy'] = $tenancy; // normalized write-back for the runtime
        }

        // Row scopes (ROW-SCOPES-PHP-PLAN §2): the tenancy machinery
        // generalized to N dimension columns. No strict sweep — a table
        // participates iff it carries the column.
        $scopes = self::normalizeScopes($config['scopes'] ?? null);
        $rowScopes = [];
        if ($scopes !== []) {
            if ($tenancy !== null && isset($scopes[$tenancy['column']])) {
                throw new ApiException(
                    'CONFIG_INVALID',
                    "Scope column '{$tenancy['column']}' duplicates the tenancy column.",
                    500,
                    null,
                    ['hint' => 'Tenancy already scopes this column — remove it from `scopes`.']
                );
            }
            $rowScopes = self::resolveRowScopes($entities, $scopes);
            $config['scopes'] = $scopes; // normalized write-back for the runtime
        }

        $maskGen = new MaskGenerator($driver, $defaultListLimit, $pkStrategies, $tenantScopes, $unscopedRoles, $rowScopes, $scopes);
        $generatedMasks = $maskGen->generate($entities);

        // Registry auto-exposure (MULTI-TENANCY-PLAN §2.7): the tenants table
        // is global, and its CRUD is gated to unscoped roles — even under
        // quickStart's wildcard access. An explicit access['<registry>'] entry
        // opts out (manual wins).
        if ($tenancy !== null && $unscopedRoles !== []) {
            $registry = $tenancy['table'];
            $accessHasRegistry = is_array($config['access'] ?? null) && isset($config['access'][$registry]);
            if (!$accessHasRegistry) {
                foreach ($generatedMasks as $name => $mask) {
                    if ($name === $registry || str_starts_with($name, $registry . '.')) {
                        $generatedMasks[$name]['roles'] = $unscopedRoles;
                    }
                }
            }
        }

        $validatorGen = new ValidatorGenerator();
        $generatedValidators = $validatorGen->generate($entities);

        $manualMasks = $config['masks'] ?? [];
        $manualValidators = $config['validators'] ?? [];

        $config['masks'] = array_merge($generatedMasks, $manualMasks);
        $config['validators'] = self::mergeValidators($generatedValidators, $manualValidators);

        // Manual / raw-SQL masks can't be parsed reliably, so they carry a
        // declaration contract validated at build time (MULTI-TENANCY-PLAN
        // §2.4 / §2.9, ROW-SCOPES §2.5) — one forgotten predicate in
        // hand-written SQL is a cross-tenant / cross-dimension leak.
        self::validateMaskContracts($config['masks'], $tenancy, $tenantScopes, $scopes, $rowScopes);

        // Built-in `tenant.provision` command (§2.7): the canonical "new
        // customer" flow — registry row + first tenant admin in one
        // transaction, gated to unscoped_roles. A manual command of the same
        // name wins, as always.
        if ($tenancy !== null && $unscopedRoles !== []) {
            $config = self::ensureTenantProvisionCommand($config, $tenancy);
        }

        // security: 'strict' — secure-by-default preset (TOKEN-EXCHANGE.md /
        // brownfield adapter guide §4). Runs again after the DB-mode
        // admin-mask merge; here it covers the entity-first path.
        self::validateStrictSecurity($config);

        $config['_shape'] = [
            'entities' => $entities,
            'generated_masks' => $generatedMasks,
            'commands' => array_keys($config['commands'] ?? []),
            'tenant_scopes' => $tenantScopes,
            'row_scopes' => $rowScopes,
        ];
        if (is_array($config['openapi'] ?? null)) {
            $config['_shape']['openapi'] = $config['openapi'];
        }

        // Auth tables (users / refresh_tokens) are secret — never
        // introspected — so the resolver above can't cover them. Hand the
        // normalized map to LoginService (auth config) and OidcVerifier
        // (auth.oidc config); they honor an explicit 'manual' at runtime.
        if ($pkAssignment !== [] && is_array($config['auth'] ?? null)) {
            $config['auth']['pk_assignment'] = $pkAssignment;
            if (is_array($config['auth']['oidc'] ?? null)) {
                $config['auth']['oidc']['pk_assignment'] = $pkAssignment;
            }
        }

        // Auth rides the scopes config (ROW-SCOPES §2.2): LoginService stamps
        // one claim per scope from users.<column> / the claim_table;
        // JwtGuard normalizes per spec; OidcVerifier resolves from the local
        // user row.
        if ($scopes !== [] && is_array($config['auth'] ?? null)) {
            $config['auth']['scopes'] = $scopes;
        }

        // SECURITY-REVIEW M2: when OIDC is enabled, `audience` is mandatory.
        // Without an `aud` binding, any token the IdP signs for any application
        // under the same issuer is accepted (confused-deputy). Fail the boot
        // instead of silently accepting all audiences. (Issuer is filled by IdP
        // presets at verify time, so it is enforced at runtime in
        // OidcVerifier::validateClaims, not here where a preset-only config
        // would false-positive.)
        $oidcCfg = $config['auth']['oidc'] ?? null;
        if (is_array($oidcCfg) && $oidcCfg !== []
            && (!isset($oidcCfg['audience']) || !is_string($oidcCfg['audience']) || $oidcCfg['audience'] === '')
        ) {
            throw new ApiException(
                'CONFIG_INVALID',
                'OIDC is enabled but `audience` is not configured.',
                500,
                null,
                ['hint' => "Set auth.oidc.audience to your application's client/app id. Without it, tokens the IdP minted for other applications under the same issuer would be accepted."]
            );
        }

        // Auth rides the tenancy config too: LoginService reads
        // `users.<column>`, issues the `tenant_id` claim, and rejects logins /
        // refreshes against a suspended tenant (MULTI-TENANCY-PLAN §2.3).
        if ($tenancy !== null && is_array($config['auth'] ?? null)) {
            $config['auth']['tenancy'] = $tenancy;

            // OIDC auto-provision under tenancy must know which tenant to
            // stamp — fail the boot, not the first login (§2.3).
            $oidc = $config['auth']['oidc'] ?? null;
            if (is_array($oidc)
                && ($oidc['auto_provision'] ?? false) === true
                && $tenancy['oidc_tenant_claim'] === null
                && $tenancy['oidc_auto_provision_tenant'] === null
            ) {
                throw new ApiException(
                    'CONFIG_INVALID',
                    'OIDC auto-provision requires a tenant source when tenancy is enabled.',
                    500,
                    null,
                    ['hint' => "Configure tenancy.oidc_tenant_claim (an IdP claim carrying the tenant id) or tenancy.oidc_auto_provision_tenant (a fixed default), or disable auth.oidc.auto_provision."]
                );
            }
        }

        return $config;
    }

    /**
     * Normalize + validate the `tenancy` config (MULTI-TENANCY-PLAN §2.2).
     * `true` (or a partial array) expands to the full array with convention
     * defaults; `false` / null disables tenancy. Returns the canonical array,
     * or null when tenancy is off.
     *
     * @return array{table:string,column:string,unscoped_roles:list<string>,enforce:string,global_tables:list<string>,check_active_per_request:bool,validate_fks:bool,oidc_tenant_claim:string|null,oidc_auto_provision_tenant:int|string|null}|null
     */
    private static function normalizeTenancy(mixed $value): ?array
    {
        if ($value === null || $value === false) {
            return null;
        }
        if ($value === true) {
            $value = [];
        }
        if (!is_array($value)) {
            throw new ApiException(
                'CONFIG_INVALID',
                'Config key tenancy must be a boolean or a config array.',
                500,
                null,
                ['expected' => "true, or ['table' => 'tenants', 'column' => 'tenant_id', 'unscoped_roles' => ['superadmin'], 'enforce' => 'strict']"]
            );
        }

        $table = isset($value['table']) && is_string($value['table']) && $value['table'] !== '' ? $value['table'] : 'tenants';
        $column = isset($value['column']) && is_string($value['column']) && $value['column'] !== '' ? $value['column'] : 'tenant_id';

        $enforce = isset($value['enforce']) && is_string($value['enforce']) ? strtolower($value['enforce']) : 'strict';
        if (!in_array($enforce, ['strict', 'relaxed'], true)) {
            throw new ApiException(
                'CONFIG_INVALID',
                "Invalid tenancy.enforce value '{$enforce}'.",
                500,
                null,
                ['expected' => "'strict' (default) or 'relaxed'"]
            );
        }

        $unscopedRoles = self::stringList($value['unscoped_roles'] ?? []);
        $globalTables = self::stringList($value['global_tables'] ?? []);

        // OIDC tenant sources (§2.3): auto-provision needs a tenant to stamp —
        // either an IdP claim carrying org context, or a fixed default.
        $oidcTenantClaim = isset($value['oidc_tenant_claim']) && is_string($value['oidc_tenant_claim']) && $value['oidc_tenant_claim'] !== ''
            ? $value['oidc_tenant_claim']
            : null;
        $oidcAutoProvisionTenant = $value['oidc_auto_provision_tenant'] ?? null;
        if (!is_int($oidcAutoProvisionTenant) && !is_string($oidcAutoProvisionTenant)) {
            $oidcAutoProvisionTenant = null;
        }

        return [
            'table' => $table,
            'column' => $column,
            'unscoped_roles' => $unscopedRoles,
            'enforce' => $enforce,
            'global_tables' => $globalTables,
            'check_active_per_request' => (bool)($value['check_active_per_request'] ?? false),
            'validate_fks' => (bool)($value['validate_fks'] ?? false),
            'oidc_tenant_claim' => $oidcTenantClaim,
            'oidc_auto_provision_tenant' => $oidcAutoProvisionTenant,
        ];
    }

    /**
     * Normalize + validate the `scopes` config (ROW-SCOPES-PHP-PLAN §2.1):
     * row-level security by dimension — tenancy's machinery generalized to
     * N discriminator columns. Keys are the columns; each entry expands to
     * the canonical spec with convention defaults. Returns a map keyed by
     * column; [] when scopes are off.
     *
     * @return array<string, array{column:string,claim:string,unscoped_roles:list<string>,multi:bool,claim_table:?array{table:string,user_column:string},nullable:string,oidc_auto_provision_value:int|string|null}>
     */
    private static function normalizeScopes(mixed $value): array
    {
        if ($value === null || $value === false || $value === []) {
            return [];
        }
        if (!is_array($value)) {
            throw new ApiException(
                'CONFIG_INVALID',
                'Config key scopes must be a column => spec map.',
                500,
                null,
                ['expected' => "['department_id' => ['claim' => 'department_id', 'unscoped_roles' => ['controller'], 'multi' => false, 'claim_table' => null, 'nullable' => 'warn']]"]
            );
        }

        $ident = '/^[A-Za-z_][A-Za-z0-9_]*$/';
        $normalized = [];
        foreach ($value as $column => $spec) {
            if (!is_string($column) || !preg_match($ident, $column)) {
                $shown = (string)$column;
                throw new ApiException(
                    'CONFIG_INVALID',
                    "Invalid scopes column '{$shown}'.",
                    500,
                    null,
                    ['expected' => 'a plain SQL identifier (the discriminator column name) as each scopes key']
                );
            }
            if ($spec === true || $spec === null) {
                $spec = [];
            }
            if (!is_array($spec)) {
                throw new ApiException(
                    'CONFIG_INVALID',
                    "Invalid scopes entry for '{$column}'.",
                    500,
                    null,
                    ['expected' => "an array like ['claim' => '{$column}', 'unscoped_roles' => [...], 'multi' => false]"]
                );
            }

            $claim = isset($spec['claim']) && is_string($spec['claim']) && $spec['claim'] !== '' ? $spec['claim'] : $column;
            $multi = (bool)($spec['multi'] ?? false);

            $claimTable = null;
            $rawClaimTable = $spec['claim_table'] ?? null;
            if ($rawClaimTable !== null && $rawClaimTable !== false) {
                $table = is_array($rawClaimTable) && isset($rawClaimTable['table']) && is_string($rawClaimTable['table']) ? $rawClaimTable['table'] : '';
                $userColumn = is_array($rawClaimTable) && isset($rawClaimTable['user_column']) && is_string($rawClaimTable['user_column']) ? $rawClaimTable['user_column'] : 'user_id';
                if (!is_array($rawClaimTable) || !preg_match($ident, $table) || !preg_match($ident, $userColumn)) {
                    throw new ApiException(
                        'CONFIG_INVALID',
                        "Invalid scopes.{$column}.claim_table.",
                        500,
                        null,
                        ['expected' => "['table' => 'user_departments', 'user_column' => 'user_id'] with plain SQL identifiers"]
                    );
                }
                if (!$multi) {
                    throw new ApiException(
                        'CONFIG_INVALID',
                        "scopes.{$column}.claim_table requires 'multi' => true.",
                        500,
                        null,
                        ['hint' => 'A membership table implies a list-valued claim; set multi => true, or drop claim_table to read the single value from users.' . $column . '.']
                    );
                }
                $claimTable = ['table' => $table, 'user_column' => $userColumn];
            }

            $nullable = strtolower((string)($spec['nullable'] ?? 'warn'));
            if (!in_array($nullable, ['warn', 'error'], true)) {
                throw new ApiException(
                    'CONFIG_INVALID',
                    "Invalid scopes.{$column}.nullable value '{$nullable}'.",
                    500,
                    null,
                    ['expected' => "'warn' (default) or 'error'"]
                );
            }

            $oidcValue = $spec['oidc_auto_provision_value'] ?? null;
            if (!is_int($oidcValue) && !is_string($oidcValue)) {
                $oidcValue = null;
            }

            $normalized[$column] = [
                'column' => $column,
                'claim' => $claim,
                'unscoped_roles' => self::stringList($spec['unscoped_roles'] ?? []),
                'multi' => $multi,
                'claim_table' => $claimTable,
                'nullable' => $nullable,
                'oidc_auto_provision_value' => $oidcValue,
            ];
        }
        return $normalized;
    }

    /**
     * Decide per table whether the framework injects the tenancy predicate
     * and stamps the discriminator (MULTI-TENANCY-PLAN §2.2). A table is
     * scoped iff it carries the tenancy column. The registry table must be
     * global (carrying the column is a build error). In strict mode, any
     * table with neither the column nor a `global_tables` entry fails the
     * boot — the guard rail that turns a forgotten `tenant_id` into a boot
     * failure rather than a silent cross-tenant leak. A nullable tenancy
     * column is a strict-mode error too (a NULL row is invisible to every
     * scoped caller — almost always a backfill bug).
     *
     * @param array<string, EntityMeta> $entities
     * @param array{table:string,column:string,enforce:string,global_tables:list<string>} $tenancy
     * @return array<string, string> table => tenancy column
     */
    /**
     * @param array<string, EntityMeta> $entities
     * @param array<string, mixed> $tenancy
     * @param list<string>|null $exposedTables Tables that are actually served;
     *        null means every introspected entity is exposed. The strict
     *        "missing tenancy column" check only sweeps this set.
     * @return array<string, string>
     */
    private static function resolveTenantScopes(array $entities, array $tenancy, ?array $exposedTables = null): array
    {
        $column = $tenancy['column'];
        $registry = $tenancy['table'];
        $globals = $tenancy['global_tables'];
        $strict = $tenancy['enforce'] === 'strict';

        $scopes = [];
        $unscopedTables = [];
        foreach ($entities as $meta) {
            $field = null;
            foreach ($meta->fields as $f) {
                if ($f->name === $column) {
                    $field = $f;
                    break;
                }
            }

            if ($meta->table === $registry) {
                if ($field !== null) {
                    throw new ApiException(
                        'CONFIG_INVALID',
                        "Tenancy registry table '{$registry}' must not carry the tenancy column '{$column}' — the registry is global by definition.",
                        500,
                        null,
                        ['hint' => "Drop '{$column}' from '{$registry}', or point tenancy.table at a different table."]
                    );
                }
                continue;
            }

            if ($field !== null) {
                if ($field->nullable && $strict) {
                    throw new ApiException(
                        'CONFIG_INVALID',
                        "Tenancy column '{$column}' on table '{$meta->table}' is nullable — a NULL row is invisible to every scoped caller.",
                        500,
                        null,
                        ['hint' => "Make '{$meta->table}.{$column}' NOT NULL (backfill first), or set tenancy.enforce => 'relaxed' during migration."]
                    );
                }
                $scopes[$meta->table] = $column;
                continue;
            }

            // Only tables that are actually served count toward the strict
            // "missing tenancy column" failure — an introspected-but-unexposed
            // table is never reachable, so it cannot leak across tenants.
            if ($exposedTables !== null && !in_array($meta->table, $exposedTables, true)) {
                continue;
            }

            if (!in_array($meta->table, $globals, true)) {
                $unscopedTables[] = $meta->table;
            }
        }

        if ($strict && $unscopedTables !== []) {
            throw new ApiException(
                'CONFIG_INVALID',
                "Tenancy is strict but these exposed tables have neither the tenancy column '{$column}' nor a global_tables entry: " . implode(', ', $unscopedTables) . '.',
                500,
                null,
                [
                    'hint' => "Add '{$column}' to each table (the isolation intent), or list genuinely-shared lookup tables under tenancy.global_tables. This list is your migration checklist.",
                ]
            );
        }

        return $scopes;
    }

    /**
     * Dimension-scope detection (ROW-SCOPES-PHP-PLAN §2.1 / P3): a table is
     * scoped by a dimension iff it carries the column — no strict sweep;
     * dimensions naturally apply to a subset of tables. A nullable scope
     * column is a boot warning (NULL rows are invisible to scoped callers)
     * or a CONFIG_INVALID when the scope opts into `nullable => 'error'`.
     *
     * @param array<string, EntityMeta> $entities
     * @param array<string, array<string, mixed>> $scopes normalized, keyed by column
     * @return array<string, array<string, array{multi: bool}>> table => column => {multi}
     */
    private static function resolveRowScopes(array $entities, array $scopes): array
    {
        $map = [];
        foreach ($entities as $meta) {
            foreach ($scopes as $column => $spec) {
                foreach ($meta->fields as $field) {
                    if ($field->name !== $column) {
                        continue;
                    }
                    if ($field->nullable) {
                        if (($spec['nullable'] ?? 'warn') === 'error') {
                            throw new ApiException(
                                'CONFIG_INVALID',
                                "Scope column '{$column}' on table '{$meta->table}' is nullable — a NULL row is invisible to every scoped caller.",
                                500,
                                null,
                                ['hint' => "Make '{$meta->table}.{$column}' NOT NULL (backfill first), or set scopes.{$column}.nullable => 'warn' during migration."]
                            );
                        }
                        self::warnOnce(
                            "[Shape] Scope column '{$column}' on table '{$meta->table}' is nullable — NULL rows are invisible to scoped callers. "
                            . "Backfill and make it NOT NULL, or set scopes.{$column}.nullable => 'error' to enforce."
                        );
                    }
                    $map[$meta->table][$column] = ['multi' => (bool)($spec['multi'] ?? false)];
                    break;
                }
            }
        }
        return $map;
    }

    /**
     * `security => 'strict'` — secure-by-default boot preset, mirroring .NET
     * ShapeApp.ValidateStrictSecurity: turn "someone forgot to gate a
     * surface" into a boot failure instead of an anonymous-reachable
     * endpoint. Three checks, each with its own hint.
     *
     * @param array<string, mixed> $config
     */
    private static function validateStrictSecurity(array $config): void
    {
        $security = $config['security'] ?? null;
        if (!is_string($security) || strtolower($security) !== 'strict') {
            return;
        }

        if ((string)($config['auth']['driver'] ?? 'none') === 'none') {
            throw new ApiException(
                'CONFIG_INVALID',
                'security: strict requires an auth driver.',
                500,
                null,
                ['hint' => "Set 'auth' => ['driver' => 'jwt', ...] — with driver 'none' every mask is reachable anonymously."]
            );
        }

        $openMasks = [];
        foreach ((is_array($config['masks'] ?? null) ? $config['masks'] : []) as $name => $mask) {
            $roles = is_array($mask) ? ($mask['roles'] ?? null) : null;
            if (!is_array($roles) || $roles === []) {
                $openMasks[] = (string)$name;
            }
        }
        if ($openMasks !== []) {
            sort($openMasks);
            throw new ApiException(
                'CONFIG_INVALID',
                'security: strict forbids masks without a roles list: ' . implode(', ', $openMasks) . '.',
                500,
                null,
                ['hint' => "A mask with empty roles is callable by every authenticated caller regardless of role. Add 'roles' to each listed mask (or to the access map entry generating it)."]
            );
        }

        if (!array_key_exists('manifest', $config) || $config['manifest'] === null) {
            throw new ApiException(
                'CONFIG_INVALID',
                'security: strict requires manifest roles.',
                500,
                null,
                ['hint' => "Set 'manifest' => [] to require any authenticated caller, or list specific roles. Absent/null leaves the manifest/OpenAPI surface open to anonymous readers."]
            );
        }
    }

    /**
     * Register the built-in `tenant.provision` command (MULTI-TENANCY-PLAN
     * §2.7) unless the app already defines one. Mirrors .NET
     * ShapeApp.EnsureTenantProvisionCommand.
     *
     * @param array<string, mixed> $config
     * @param array<string, mixed> $tenancy
     * @return array<string, mixed>
     */
    private static function ensureTenantProvisionCommand(array $config, array $tenancy): array
    {
        $commands = is_array($config['commands'] ?? null) ? $config['commands'] : [];
        if (isset($commands['tenant.provision'])) {
            return $config;
        }
        $authConfig = is_array($config['auth'] ?? null) ? $config['auth'] : [];
        $commands['tenant.provision'] = static fn(array $params, CommandContext $context): array =>
            self::tenantProvision($tenancy, $authConfig, $params, $context);
        $config['commands'] = $commands;
        return $config;
    }

    /**
     * The canonical "new customer" flow (§2.7), atomic in one transaction:
     * insert the registry row + the tenant's first admin user (password or
     * oid). Returns {tenant_id, user_id}. Gated to unscoped_roles.
     *
     * @param array<string, mixed> $tenancy
     * @param array<string, mixed> $authConfig
     * @param array<string, mixed> $params
     * @return array<string, mixed>
     */
    private static function tenantProvision(array $tenancy, array $authConfig, array $params, CommandContext $context): array
    {
        $authContext = $context->getAuthContext();
        $role = isset($authContext['role']) ? (string)$authContext['role'] : '';
        $unscopedRoles = is_array($tenancy['unscoped_roles'] ?? null) ? $tenancy['unscoped_roles'] : [];
        if ($role === '' || !in_array($role, $unscopedRoles, true)) {
            throw new ApiException('AUTH_FORBIDDEN', 'Tenant provisioning requires an unscoped role.', 403, null, [
                'hint' => 'Use one of the roles listed in tenancy.unscoped_roles.',
            ]);
        }

        $tenantData = is_array($params['tenant'] ?? null) ? $params['tenant'] : null;
        if ($tenantData === null) {
            throw new ApiException('COMMAND_VALIDATION_ERROR', 'Field tenant is required.', 400, null, [
                'expected' => ['tenant' => ['name' => 'string'], 'user' => ['email' => 'string', 'password' => 'string']],
            ]);
        }
        $userData = is_array($params['user'] ?? null) ? $params['user'] : null;
        if ($userData === null) {
            throw new ApiException('COMMAND_VALIDATION_ERROR', 'Field user is required.', 400, null, [
                'expected' => ['user' => ['email' => 'string', 'password' => 'string (or oid for OIDC-only)']],
            ]);
        }

        $column = (string)($tenancy['column'] ?? 'tenant_id');
        if (array_key_exists($column, $tenantData)) {
            throw new ApiException('VALIDATION_ERROR', "'{$column}' is reserved on tenant rows.", 400);
        }

        $registry = (string)($tenancy['table'] ?? 'tenants');
        $usersTable = (string)($authConfig['users_table'] ?? 'users');
        $oidc = is_array($authConfig['oidc'] ?? null) ? $authConfig['oidc'] : [];
        $lookupColumn = (string)($oidc['users_lookup_column'] ?? 'oid');
        $pkAssignment = $authConfig['pk_assignment'] ?? null;

        $db = $context->database();
        $context->beginTransaction();
        try {
            $tenantId = self::insertCommandRow($db, $registry, $tenantData, $pkAssignment);

            $userRow = $userData;
            $userRow[$column] = $tenantId;
            $email = trim((string)($userRow['email'] ?? ''));
            if ($email === '') {
                throw new ApiException('COMMAND_VALIDATION_ERROR', 'User email is required.', 400);
            }
            if (trim((string)($userRow['display_name'] ?? '')) === '') {
                $userRow['display_name'] = strstr($email, '@', true) ?: $email;
            }

            $userRole = trim((string)($userRow['role'] ?? 'admin'));
            if ($userRole === '') {
                $userRole = 'admin';
            }
            if (in_array($userRole, $unscopedRoles, true)) {
                throw new ApiException('AUTH_FORBIDDEN', 'Tenant admin cannot be an unscoped role.', 403, null, [
                    'hint' => 'Pick a tenant-scoped role for the first tenant admin.',
                ]);
            }
            $userRow['role'] = $userRole;

            // oid / lookup-column normalization: accept `oid` even when the
            // configured lookup column is named differently.
            $oidValue = $userRow[$lookupColumn] ?? null;
            if ($oidValue === null && isset($userRow['oid'])) {
                $oidValue = $userRow['oid'];
                if ($lookupColumn !== 'oid') {
                    unset($userRow['oid']);
                    $userRow[$lookupColumn] = $oidValue;
                }
            }

            if (isset($userRow['password'])) {
                $password = (string)$userRow['password'];
                if ($password === '') {
                    throw new ApiException('COMMAND_VALIDATION_ERROR', 'User password is empty.', 400);
                }
                $minLength = (int)($authConfig['password_min_length'] ?? 8);
                if (strlen($password) < $minLength) {
                    throw new ApiException('COMMAND_VALIDATION_ERROR', "Password must have at least {$minLength} characters.", 400);
                }
                unset($userRow['password']);
                $userRow['password_hash'] = password_hash($password, PASSWORD_BCRYPT);
            } elseif ($oidValue !== null) {
                // OIDC-only admin: sentinel that can never pass password_verify.
                $userRow['password_hash'] = '!locked-oidc-only!';
            } else {
                throw new ApiException('COMMAND_VALIDATION_ERROR', 'User must include either password or oid.', 400, null, [
                    'expected' => ['password' => 'string', $lookupColumn => 'string'],
                ]);
            }

            $userId = self::insertCommandRow($db, $usersTable, $userRow, $pkAssignment);
            $context->commit();

            return ['tenant_id' => $tenantId, 'user_id' => $userId];
        } catch (\Throwable $e) {
            if ($context->inTransaction()) {
                $context->rollback();
            }
            throw $e;
        }
    }

    /**
     * INSERT with validated identifiers (client-supplied keys are column
     * names — reject anything that isn't a plain identifier before it can
     * reach the SQL text), honoring a forced-manual pk_assignment.
     *
     * @param array<string, mixed> $data
     */
    private static function insertCommandRow(Database $db, string $table, array $data, mixed $pkAssignment): int
    {
        if (!preg_match('/^[A-Za-z_][A-Za-z0-9_]*$/', $table)) {
            throw new ApiException('AUTH_CONFIG_ERROR', 'Invalid table name.', 500);
        }
        $columns = [];
        foreach (array_keys($data) as $col) {
            $name = (string)$col;
            if (!preg_match('/^[A-Za-z_][A-Za-z0-9_]*$/', $name)) {
                throw new ApiException('COMMAND_VALIDATION_ERROR', "Invalid column name '{$name}'.", 400);
            }
            $columns[] = $name;
        }
        if ($columns === []) {
            throw new ApiException('COMMAND_VALIDATION_ERROR', 'Insert requires at least one column.', 400);
        }

        if (Database::resolvePkAssignmentMode($pkAssignment, $table) === 'manual') {
            return $db->insertWithManualPk($table, 'id', $data);
        }

        $sql = 'INSERT INTO ' . $db->quoteIdentifier($table)
            . ' (' . implode(', ', array_map(static fn(string $c): string => $db->quoteIdentifier($c), $columns)) . ')'
            . ' VALUES (' . implode(', ', array_map(static fn(string $c): string => ':' . $c, $columns)) . ')';
        return $db->insertReturningId($sql, $data);
    }

    /**
     * Build-time declaration contract for masks under tenancy (MULTI-TENANCY-PLAN
     * §2.4 / §2.9). Mirrors .NET TenancyResolver.ValidateMaskContracts:
     *
     *  - `tenant_scope => '<col>'` — author put `:__tenant` in the SQL; error
     *    when the placeholder is missing (writable masks exempt: their SQL is
     *    only an existence probe, tenant enforcement rides the write path).
     *  - `tenant_scope => false` — deliberate opt-out; its roles must then be
     *    a subset of `unscoped_roles` (an opted-out mask reachable by a tenant
     *    role is a leak by construction).
     *  - no declaration + strict mode + SQL references a scoped table name →
     *    CONFIG_INVALID. Table-name scan is a documented best-effort heuristic.
     *  - `cache_control` containing `public` on a scoped mask → CONFIG_INVALID
     *    (same URL, different tenants, one shared proxy cache = leak).
     *
     * Row-scope leg (ROW-SCOPES-PHP-PLAN §2.5), relaxed by design:
     *
     *  - `row_scopes => ['<col>', ...]` — each `:__scope_<col>` placeholder
     *    must appear in the SQL (writable masks exempt).
     *  - `row_scopes => false` — opt-out; roles must be ⊆ the union of the
     *    touched dimensions' unscoped_roles.
     *  - no declaration + SQL references a dimension-scoped table →
     *    boot WARNING (error_log), never a boot error.
     *
     * @param array<string, mixed> $masks
     * @param array<string, mixed>|null $tenancy
     * @param array<string, string> $tenantScopes table => tenancy column
     * @param array<string, array<string, mixed>> $scopes normalized `scopes` config keyed by column
     * @param array<string, array<string, array{multi: bool}>> $rowScopes table => column => {multi}
     */
    private static function validateMaskContracts(array $masks, ?array $tenancy, array $tenantScopes, array $scopes = [], array $rowScopes = []): void
    {
        if ($tenancy === null && $scopes === []) {
            return;
        }
        $strict = $tenancy !== null && ($tenancy['enforce'] ?? 'strict') === 'strict';
        $unscopedRoles = $tenancy !== null && is_array($tenancy['unscoped_roles'] ?? null) ? $tenancy['unscoped_roles'] : [];
        $globals = $tenancy !== null && is_array($tenancy['global_tables'] ?? null) ? $tenancy['global_tables'] : [];
        $scopedTables = array_values(array_diff(array_keys($tenantScopes), $globals));

        // Dimension → its scoped tables (drives the relaxed row-scope checks).
        $dimTables = [];
        foreach ($rowScopes as $table => $cols) {
            foreach (array_keys($cols) as $col) {
                $dimTables[$col][] = $table;
            }
        }

        foreach ($masks as $name => $mask) {
            if (!is_array($mask)) {
                continue;
            }
            $scope = $mask['tenant_scope'] ?? null;
            $declared = is_string($scope) && trim($scope) !== '' ? $scope : null;
            $optOut = $scope === false;
            $sql = is_string($mask['sql'] ?? null) ? $mask['sql'] : '';
            $sqlUnscoped = is_string($mask['sql_unscoped'] ?? null) ? $mask['sql_unscoped'] : '';
            $scopedRefs = self::referencesScopedTable($sql, $scopedTables)
                || self::referencesScopedTable($sqlUnscoped, $scopedTables);
            $writable = (bool)($mask['writable'] ?? false);

            // Row-scope declaration: generated masks carry the column =>
            // {multi} map; manual masks may declare a plain column list;
            // `false` is a deliberate opt-out.
            $rowDecl = $mask['row_scopes'] ?? null;
            $rowOptOut = $rowDecl === false;
            $rowCols = [];
            if (is_array($rowDecl)) {
                $rowCols = array_is_list($rowDecl)
                    ? array_values(array_filter($rowDecl, static fn($c): bool => is_string($c) && $c !== ''))
                    : array_values(array_filter(array_keys($rowDecl), 'is_string'));
            }
            // Dimensions whose scoped tables the SQL references (heuristic).
            $dimRefs = [];
            foreach ($dimTables as $col => $tables) {
                if (self::referencesScopedTable($sql, $tables) || self::referencesScopedTable($sqlUnscoped, $tables)) {
                    $dimRefs[] = $col;
                }
            }

            $cache = $mask['cache_control'] ?? null;
            if (is_string($cache) && stripos($cache, 'public') !== false
                && ($declared !== null || $scopedRefs || $rowCols !== [] || $dimRefs !== [])
            ) {
                throw new ApiException(
                    'CONFIG_INVALID',
                    "Mask '{$name}' uses public cache-control while tenant-scoped.",
                    500,
                    null,
                    ['hint' => 'Remove `public` from cache_control (private stays fine — browser caches are per-user) or drop tenant/row scoping.']
                );
            }

            if ($tenancy !== null) {
                if ($declared !== null) {
                    if (!$writable && preg_match('/[:@]__tenant\b/i', $sql) !== 1) {
                        throw new ApiException(
                            'CONFIG_INVALID',
                            "Mask '{$name}' declares tenant scope but SQL does not reference `__tenant`.",
                            500,
                            null,
                            ['hint' => 'Add `:__tenant` (or `@__tenant`) to the scoped SQL.']
                        );
                    }
                } elseif (!$optOut && $scopedRefs && $strict) {
                    throw new ApiException(
                        'CONFIG_INVALID',
                        "Mask '{$name}' references tenant-scoped tables but does not declare tenant scope.",
                        500,
                        null,
                        ['hint' => "Declare 'tenant_scope' => '<column>' (with :__tenant in the SQL), 'tenant_scope' => false for a deliberate cross-tenant mask, or move the table(s) to tenancy.global_tables if they are intentionally shared."]
                    );
                } elseif ($optOut) {
                    $roles = is_array($mask['roles'] ?? null) ? $mask['roles'] : [];
                    if ($roles === [] || array_diff($roles, $unscopedRoles) !== []) {
                        throw new ApiException(
                            'CONFIG_INVALID',
                            "Mask '{$name}' opts out of tenant scoping but is reachable by a scoped role.",
                            500,
                            null,
                            ['hint' => 'Restrict the mask to roles listed in tenancy.unscoped_roles.']
                        );
                    }
                }
            }

            if ($scopes === []) {
                continue;
            }
            if ($rowCols !== []) {
                if (!$writable) {
                    foreach ($rowCols as $col) {
                        if (preg_match('/[:@]__scope_' . preg_quote($col, '/') . '\b/i', $sql) !== 1) {
                            throw new ApiException(
                                'CONFIG_INVALID',
                                "Mask '{$name}' declares row scope '{$col}' but SQL does not reference `__scope_{$col}`.",
                                500,
                                null,
                                ['hint' => "Add `:__scope_{$col}` (or `@__scope_{$col}`) to the scoped SQL."]
                            );
                        }
                    }
                }
            } elseif ($rowOptOut) {
                $roles = is_array($mask['roles'] ?? null) ? $mask['roles'] : [];
                $checkCols = $dimRefs !== [] ? $dimRefs : array_keys($scopes);
                $union = [];
                foreach ($checkCols as $col) {
                    $union = array_merge($union, self::stringList($scopes[$col]['unscoped_roles'] ?? []));
                }
                if ($roles === [] || array_diff($roles, array_unique($union)) !== []) {
                    throw new ApiException(
                        'CONFIG_INVALID',
                        "Mask '{$name}' opts out of row scoping but is reachable by a scoped role.",
                        500,
                        null,
                        ['hint' => 'Restrict the mask to roles listed in the unscoped_roles of every dimension it touches.']
                    );
                }
            } elseif ($rowDecl === null && $dimRefs !== []) {
                // Undeclared manual mask touching a dimension-scoped table:
                // dimensions default relaxed — warn, never fail the boot
                // (tenancy's strict rule above is unchanged).
                foreach ($dimRefs as $col) {
                    self::warnOnce(
                        "[Shape] Mask '{$name}' references tables scoped by '{$col}' but declares no row_scopes — its results are not dimension-filtered. "
                        . "Declare 'row_scopes' => ['{$col}'] (with :__scope_{$col} in the SQL), or 'row_scopes' => false for a deliberate cross-dimension mask."
                    );
                }
            }
        }
    }

    /**
     * Best-effort heuristic: does the SQL text mention any scoped table name
     * as a whole word? Can false-positive on strings — the fix is always
     * "declare intent explicitly", which is the point (§2.4).
     *
     * @param list<string> $scopedTables
     */
    private static function referencesScopedTable(string $sql, array $scopedTables): bool
    {
        if ($sql === '' || $scopedTables === []) {
            return false;
        }
        foreach ($scopedTables as $table) {
            if (preg_match('/\b' . preg_quote($table, '/') . '\b/i', $sql) === 1) {
                return true;
            }
        }
        return false;
    }

    /**
     * @return list<string>
     */
    private static function stringList(mixed $value): array
    {
        if (!is_array($value)) {
            return [];
        }
        $out = [];
        foreach ($value as $item) {
            if (is_string($item) && $item !== '') {
                $out[] = $item;
            }
        }
        return $out;
    }

    /**
     * Normalize + validate `pk_assignment` (MANUAL-PK-PLAN §2.2): a string
     * shorthand applies one mode to the whole database; the map form has a
     * '*' default plus per-table overrides. Returns the canonical map.
     *
     * @return array<string, string>
     */
    private static function normalizePkAssignment(mixed $value): array
    {
        if ($value === null) {
            return [];
        }
        if (is_string($value)) {
            $value = ['*' => $value];
        }
        if (!is_array($value)) {
            throw new ApiException(
                'CONFIG_INVALID',
                'Config key pk_assignment must be a string or a table => mode map.',
                500,
                null,
                ['expected' => "'auto' | 'db' | 'manual', or ['*' => 'auto', '<table>' => 'manual', ...]"]
            );
        }

        $normalized = [];
        foreach ($value as $table => $mode) {
            if (!is_string($table) || $table === ''
                || !is_string($mode) || !in_array($mode, ['auto', 'db', 'manual'], true)
            ) {
                $shown = is_scalar($mode) ? (string)$mode : gettype($mode);
                throw new ApiException(
                    'CONFIG_INVALID',
                    "Invalid pk_assignment entry '{$table}' => '{$shown}'.",
                    500,
                    null,
                    [
                        'expected' => "mode 'auto' | 'db' | 'manual' keyed by table name (or '*' for the database default)",
                        'hint' => "'auto' detects manual-PK tables from the schema; 'manual' forces framework MAX+1 allocation; 'db' trusts the database (identity / sequence / trigger).",
                    ]
                );
            }
            $normalized[$table] = $mode;
        }
        return $normalized;
    }

    /**
     * Decide per table whether the framework must allocate the PK on insert.
     *
     * Auto rule (mode 'auto', the default): a single-column int-family PK
     * (int/bigint/smallint/tinyint/mediumint, or decimal/numeric with scale
     * 0 — legacy MSSQL schemas often use numeric(9,0)) that is neither
     * auto-increment nor defaulted has nothing assigning it → 'manual'.
     * A PK defaulted to `NEXT VALUE FOR seq` / `NEWID()` or an identity
     * column is correctly left to the DB.
     *
     * Forcing 'manual' onto a composite or non-int PK is a build-time error
     * — MAX+1 has no meaning there and we never guess.
     *
     * @param array<string, EntityMeta> $entities
     * @param array<string, string> $pkAssignment
     * @return array<string, string> table => 'manual'; db-assigned tables are omitted
     */
    private static function resolvePkStrategies(array $entities, array $pkAssignment): array
    {
        $strategies = [];
        foreach ($entities as $meta) {
            $mode = $pkAssignment[$meta->table] ?? $pkAssignment['*'] ?? 'auto';
            if ($mode === 'db') {
                continue;
            }

            $pkFields = array_values(array_filter(
                $meta->fields,
                static fn(FieldMeta $f): bool => $f->isPrimary
            ));
            $pk = count($pkFields) === 1 ? $pkFields[0] : null;
            $type = $pk !== null ? strtolower($pk->dbType) : '';
            $intFamily = $pk !== null && (
                in_array($type, ['int', 'integer', 'bigint', 'smallint', 'tinyint', 'mediumint'], true)
                || (in_array($type, ['decimal', 'numeric'], true) && $pk->scale === 0)
            );

            if ($mode === 'manual') {
                if ($pk === null || !$intFamily) {
                    $reason = count($pkFields) > 1
                        ? 'a composite primary key'
                        : ($pk === null
                            ? 'no primary key'
                            : "a non-integer primary key ({$pk->name}: {$pk->dbType})");
                    throw new ApiException(
                        'CONFIG_INVALID',
                        "pk_assignment forces 'manual' on table '{$meta->table}', which has {$reason}.",
                        500,
                        null,
                        ['hint' => "Manual PK allocation computes MAX(pk)+1 and requires a single int-family primary key (int/bigint/smallint/tinyint, or decimal/numeric with scale 0). Use 'db' for this table."]
                    );
                }
                $strategies[$meta->table] = 'manual';
                continue;
            }

            if ($pk !== null && $intFamily && !$pk->isAutoIncrement && !$pk->hasDefault) {
                $strategies[$meta->table] = 'manual';
            }
        }
        return $strategies;
    }

    private static function detectDriver(array $dbConfig): string
    {
        $dsn = (string)($dbConfig['dsn'] ?? '');
        if (str_starts_with($dsn, 'pgsql:')) {
            return 'pgsql';
        }
        if (str_starts_with($dsn, 'sqlite:')) {
            return 'sqlite';
        }
        if (str_starts_with($dsn, 'sqlsrv:')) {
            return 'sqlsrv';
        }
        return 'mysql';
    }

    /**
     * Merge auto-generated validators with manual validators.
     * Manual field validators override auto ones. Hooks always from manual.
     */
    private static function mergeValidators(array $auto, array $manual): array
    {
        $merged = $auto;

        foreach ($manual as $key => $manualConfig) {
            if (!isset($merged[$key])) {
                $merged[$key] = $manualConfig;
                continue;
            }

            $autoValidators = $merged[$key]['validators'] ?? [];
            $manualValidators = $manualConfig['validators'] ?? [];
            $merged[$key]['validators'] = array_merge($autoValidators, $manualValidators);

            if (isset($manualConfig['hooks'])) {
                $merged[$key]['hooks'] = $manualConfig['hooks'];
            }
        }

        return $merged;
    }

    /**
     * Clear all cached entity metadata.
     * Useful after DB schema changes in fromDatabase mode.
     */
    public static function clearCache(string $configPath): void
    {
        $config = self::loadConfig($configPath);
        foreach (['entities', 'database'] as $mode) {
            $cachePath = self::resolveCachePath($config, $configPath, $mode);
            if ($cachePath !== null && is_file($cachePath)) {
                @unlink($cachePath);
            }
        }
    }
}
