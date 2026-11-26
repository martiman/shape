<?php

declare(strict_types=1);

namespace ShapeRuntime\Auth;

use ShapeRuntime\Core\ApiException;
use ShapeRuntime\Core\Database;

/**
 * Verifies RS256 / RS384 / RS512 JWTs against an OIDC provider's JWKS
 * endpoint (e.g. Entra ID / Azure AD, Auth0, Google, Okta, Cognito).
 *
 * Used as the secondary verification path inside JwtGuard. The primary path
 * — HS256 tokens issued by Shape's own LoginService against the local users
 * table — is unchanged. When an inbound token's `alg` header matches one
 * of the configured RSA algorithms, JwtGuard delegates here.
 *
 * Configuration shape (under `auth.oidc`):
 *
 *   'jwks_uri'      => 'https://login.microsoftonline.com/<tenant>/discovery/v2.0/keys'
 *   'issuer'        => 'https://login.microsoftonline.com/<tenant>/v2.0'
 *   'audience'      => '<your-app-client-id>'
 *   'role_claim'    => 'roles'        (default 'role'; for Entra use 'roles' — array)
 *   'role_mapping'  => [ 'BackendAdmin' => 'admin', 'BackendUser' => 'user' ]
 *   'id_claim'      => 'oid'          (default 'sub')
 *   'email_claim'   => 'preferred_username'  (default 'email')
 *   'jwks_cache_ttl'=> 3600           (seconds, default 3600)
 *   'jwks_cache_dir'=> '/tmp/shape_jwks'  (default sys_get_temp_dir())
 *   'algorithms'    => ['RS256']      (default; whitelist — never trust the
 *                                       token's own alg without a config check)
 *
 * Security notes:
 *   - The accepted algorithm list is config-driven; tokens with `alg: none`
 *     or unexpected algorithms are rejected before any signature work.
 *   - Issuer (`iss`) and audience (`aud`) are always validated when configured.
 *   - JWKS responses are cached to disk with a TTL; expired cache triggers a
 *     fresh fetch. A failed fetch falls back to the stale cache to keep the
 *     API up if the IdP is briefly unreachable.
 */
final class OidcVerifier
{
    private const SUPPORTED_ALGORITHMS = [
        'RS256' => 'sha256',
        'RS384' => 'sha384',
        'RS512' => 'sha512',
    ];

    /**
     * @param array<string, mixed> $config The `auth.oidc` block
     * @param ?Database $database Optional. When supplied, the verifier
     *   resolves the IdP-issued `oid` to the local `users.id` after
     *   signature/claim verification — so audit columns (created_by,
     *   updated_by) and audit_log.actor_id always store users.id, not
     *   the IdP's foreign identifier. Without a Database, `id` stays as
     *   the IdP's claim value (legacy behavior).
     */
    public function __construct(
        array $config,
        private readonly ?Database $database = null,
    ) {
        // Apply IdP preset (entra / auth0 / okta / google / cognito) before
        // anything else reads the config. The preset fills in the
        // boilerplate jwks_uri / issuer / claim names; user values still
        // win on conflict so per-app overrides keep working.
        $this->config = self::applyIdpPreset($config);
    }

    private readonly array $config;

    /**
     * Expand an IdP preset name into the seven boilerplate fields every
     * Entra / Auth0 / Okta / Google / Cognito user types verbatim.
     *
     * Required preset inputs:
     *   entra:    tenant_id
     *   cognito:  region, user_pool_id
     *   auth0:    domain
     *   okta:     domain
     *   google:   (none — issuer/jwks are static)
     *
     * User config wins over preset defaults — explicit overrides keep
     * working. Unknown preset names are ignored (no-op) so the rest of
     * the OIDC config still loads as-is.
     *
     * @param array<string, mixed> $config
     * @return array<string, mixed>
     */
    private static function applyIdpPreset(array $config): array
    {
        $preset = isset($config['preset']) && is_string($config['preset'])
            ? strtolower(trim($config['preset']))
            : '';
        if ($preset === '') {
            return $config;
        }

        $defaults = match ($preset) {
            'entra', 'azure', 'azuread' => [
                'jwks_uri'    => 'https://login.microsoftonline.com/' . (string)($config['tenant_id'] ?? '<tenant_id>') . '/discovery/v2.0/keys',
                'issuer'      => 'https://login.microsoftonline.com/' . (string)($config['tenant_id'] ?? '<tenant_id>') . '/v2.0',
                'algorithms'  => ['RS256'],
                'id_claim'    => 'oid',
                'email_claim' => 'preferred_username',
                'role_claim'  => 'roles',
            ],
            'auth0' => [
                'jwks_uri'    => 'https://' . (string)($config['domain'] ?? '<domain>') . '/.well-known/jwks.json',
                'issuer'      => 'https://' . (string)($config['domain'] ?? '<domain>') . '/',
                'algorithms'  => ['RS256'],
                'id_claim'    => 'sub',
                'email_claim' => 'email',
                // Auth0 typically namespaces role claims; the operator must
                // override role_claim to match their tenant.
            ],
            'okta' => [
                'jwks_uri'    => 'https://' . (string)($config['domain'] ?? '<domain>') . '/oauth2/default/v1/keys',
                'issuer'      => 'https://' . (string)($config['domain'] ?? '<domain>') . '/oauth2/default',
                'algorithms'  => ['RS256'],
                'id_claim'    => 'sub',
                'email_claim' => 'email',
                'role_claim'  => 'groups',
            ],
            'google' => [
                'jwks_uri'    => 'https://www.googleapis.com/oauth2/v3/certs',
                'issuer'      => 'https://accounts.google.com',
                'algorithms'  => ['RS256'],
                'id_claim'    => 'sub',
                'email_claim' => 'email',
            ],
            'cognito' => [
                'jwks_uri'    => 'https://cognito-idp.' . (string)($config['region'] ?? '<region>') . '.amazonaws.com/' . (string)($config['user_pool_id'] ?? '<user_pool_id>') . '/.well-known/jwks.json',
                'issuer'      => 'https://cognito-idp.' . (string)($config['region'] ?? '<region>') . '.amazonaws.com/' . (string)($config['user_pool_id'] ?? '<user_pool_id>'),
                'algorithms'  => ['RS256'],
                'id_claim'    => 'sub',
                'email_claim' => 'email',
                'role_claim'  => 'cognito:groups',
            ],
            default => [],
        };

        // User config wins on conflict — preset only fills *missing* keys.
        // (Note: array_merge would let preset win, which we don't want.)
        return $config + $defaults;
    }

    /**
     * Verify a token and return its claims plus a Shape-shaped auth context
     * (id / email / role mapped from the configured claim names).
     *
     * @param array<string, mixed> $header  Decoded JWT header
     * @param array<string, mixed> $payload Decoded JWT payload
     * @return array<string, mixed>
     */
    public function verify(string $signingInput, string $signatureRaw, array $header, array $payload): array
    {
        $alg = (string)($header['alg'] ?? '');
        $allowed = $this->allowedAlgorithms();
        if (!in_array($alg, $allowed, true)) {
            throw new ApiException('AUTH_INVALID', "Algorithm '{$alg}' is not allowed for OIDC tokens.", 401);
        }

        $kid = isset($header['kid']) ? (string)$header['kid'] : '';
        $publicPem = $this->resolvePublicKey($kid);

        $hashAlgo = self::SUPPORTED_ALGORITHMS[$alg];
        $ok = openssl_verify($signingInput, $signatureRaw, $publicPem, $hashAlgo);
        if ($ok !== 1) {
            throw new ApiException('AUTH_INVALID', 'OIDC token signature mismatch.', 401);
        }

        $this->validateClaims($payload);

        $context = $this->mapClaims($payload);

        // Option B: resolve token's `oid` (foreign key to the IdP) to the
        // local users.id (the framework's canonical actor identifier).
        // Audit columns and audit_log.actor_id then always reference
        // users.id, so FKs work uniformly across local + OIDC auth paths.
        if ($this->database !== null) {
            $context = $this->resolveLocalUserId($context);
        } elseif ($this->tenancyConfig() !== null) {
            // SECURITY-REVIEW L9: under tenancy, the tenant is always derived
            // from the local user row — never from the IdP token. Without a
            // Database to resolve that row, a token-supplied `tenant_id` must not
            // be trusted; drop it so the scoped-role path fails closed instead of
            // honoring an attacker-chosen tenant.
            unset($context['tenant_id']);
        }

        return $context;
    }

    /**
     * Whether to even try this verifier. Used by JwtGuard to short-circuit:
     * if the inbound token's alg is one of ours, dispatch here; otherwise
     * stay on the HS256 path.
     */
    public function handlesAlgorithm(string $alg): bool
    {
        return in_array($alg, $this->allowedAlgorithms(), true);
    }

    /** @return list<string> */
    private function allowedAlgorithms(): array
    {
        $configured = $this->config['algorithms'] ?? ['RS256'];
        if (!is_array($configured) || $configured === []) {
            return ['RS256'];
        }
        $out = [];
        foreach ($configured as $alg) {
            if (is_string($alg) && isset(self::SUPPORTED_ALGORITHMS[$alg])) {
                $out[] = $alg;
            }
        }
        return $out !== [] ? $out : ['RS256'];
    }

    private function resolvePublicKey(string $kid): string
    {
        $jwks = $this->loadJwks();
        $key = $this->findKey($jwks, $kid);
        if ($key === null) {
            // Cache might be stale — try a fresh fetch once before giving up.
            $jwks = $this->loadJwks(forceFresh: true);
            $key = $this->findKey($jwks, $kid);
        }
        if ($key === null) {
            throw new ApiException('AUTH_INVALID', "OIDC signing key '{$kid}' not found in JWKS.", 401);
        }
        return $this->jwkToPem($key);
    }

    /**
     * @return array{keys: list<array<string, mixed>>}
     */
    private function loadJwks(bool $forceFresh = false): array
    {
        $uri = (string)($this->config['jwks_uri'] ?? '');
        if ($uri === '') {
            throw new ApiException('AUTH_CONFIG_ERROR', "OIDC 'jwks_uri' is not configured.", 500);
        }

        $cacheDir = (string)($this->config['jwks_cache_dir'] ?? (sys_get_temp_dir() . '/shape_jwks'));
        // SECURITY-REVIEW H2: the JWKS cache is trusted on read, so a
        // world-writable cache in a shared temp dir lets a local attacker
        // pre-plant a file with their own RSA key and forge OIDC tokens. Create
        // the dir 0700 and refuse to use it if it is group/other-writable.
        $this->ensureSecureCacheDir($cacheDir);
        $cacheFile = $cacheDir . '/' . hash('sha256', $uri) . '.json';
        $ttl = (int)($this->config['jwks_cache_ttl'] ?? 3600);

        if (!$forceFresh && is_file($cacheFile) && self::cacheFileIsTrusted($cacheFile)
            && (time() - (int)filemtime($cacheFile)) < $ttl) {
            $cached = @file_get_contents($cacheFile);
            if (is_string($cached)) {
                $decoded = json_decode($cached, true);
                if (is_array($decoded) && isset($decoded['keys']) && is_array($decoded['keys'])) {
                    return $decoded;
                }
            }
        }

        $fresh = $this->fetchJwks($uri);
        if ($fresh === null) {
            // Fetch failed — fall back to stale cache within a bounded window so
            // a momentary IdP outage doesn't take the whole API down, but a
            // revoked key can't validate tokens forever.
            $maxStale = (int)($this->config['jwks_max_stale_ttl'] ?? 0);
            if ($maxStale <= 0) {
                $maxStale = $ttl * 8;
            }
            if (is_file($cacheFile) && self::cacheFileIsTrusted($cacheFile)
                && (time() - (int)filemtime($cacheFile)) <= $maxStale) {
                $stale = @file_get_contents($cacheFile);
                if (is_string($stale)) {
                    $decoded = json_decode($stale, true);
                    if (is_array($decoded) && isset($decoded['keys']) && is_array($decoded['keys'])) {
                        return $decoded;
                    }
                }
            }
            throw new ApiException('AUTH_CONFIG_ERROR', 'Failed to fetch JWKS and no fresh-enough cached copy available.', 503);
        }

        @file_put_contents($cacheFile, json_encode($fresh), LOCK_EX);
        @chmod($cacheFile, 0600);
        return $fresh;
    }

    /**
     * Create the JWKS cache directory 0700 and reject it if it is
     * group/other-writable — a writable cache dir on a shared host is a
     * key-substitution vector (SECURITY-REVIEW H2).
     */
    private function ensureSecureCacheDir(string $cacheDir): void
    {
        if (!is_dir($cacheDir)) {
            @mkdir($cacheDir, 0700, true);
        } else {
            @chmod($cacheDir, 0700);
        }
        // Windows has no POSIX mode bits; clearstatcache so a freshly chmod'd
        // dir reports its new perms.
        if (DIRECTORY_SEPARATOR === '\\') {
            return;
        }
        clearstatcache(true, $cacheDir);
        $perms = @fileperms($cacheDir);
        if ($perms !== false && ($perms & 0o022) !== 0) {
            throw new ApiException(
                'AUTH_CONFIG_ERROR',
                'OIDC JWKS cache directory is group/other-writable and cannot be trusted.',
                500,
                null,
                ['hint' => "Point auth.oidc.jwks_cache_dir at a private directory owned by the service account (mode 0700). Current: '{$cacheDir}'."]
            );
        }
    }

    /**
     * A cache file is trusted only when it is not group/other-writable. Blocks
     * reading a JWKS an attacker planted or tampered with (SECURITY-REVIEW H2).
     * Always true on Windows (ACL-governed, no POSIX mode bits).
     */
    private static function cacheFileIsTrusted(string $path): bool
    {
        if (DIRECTORY_SEPARATOR === '\\') {
            return true;
        }
        clearstatcache(true, $path);
        $perms = @fileperms($path);
        if ($perms === false) {
            return false;
        }
        return ($perms & 0o022) === 0;
    }

    /**
     * @return array{keys: list<array<string, mixed>>}|null
     */
    private function fetchJwks(string $uri): ?array
    {
        $ctx = stream_context_create([
            'http' => ['timeout' => 5, 'method' => 'GET'],
            'https' => ['timeout' => 5, 'method' => 'GET'],
        ]);
        $raw = @file_get_contents($uri, false, $ctx);
        if (!is_string($raw) || $raw === '') {
            return null;
        }
        $decoded = json_decode($raw, true);
        if (!is_array($decoded) || !isset($decoded['keys']) || !is_array($decoded['keys'])) {
            return null;
        }
        return $decoded;
    }

    /**
     * @param array{keys: list<array<string, mixed>>} $jwks
     * @return array<string, mixed>|null
     */
    private function findKey(array $jwks, string $kid): ?array
    {
        foreach ($jwks['keys'] as $key) {
            if (!is_array($key)) {
                continue;
            }
            // Match by kid; if the token has no kid and there's only one
            // key in the set, fall back to that key.
            if ($kid !== '' && (string)($key['kid'] ?? '') === $kid) {
                return $key;
            }
        }
        if ($kid === '' && count($jwks['keys']) === 1 && is_array($jwks['keys'][0])) {
            return $jwks['keys'][0];
        }
        return null;
    }

    /**
     * Convert a JWK with `n` (modulus) and `e` (exponent) into a PEM-encoded
     * RSA public key for openssl_verify.
     *
     * RSA SubjectPublicKeyInfo DER structure:
     *   SEQUENCE {
     *     SEQUENCE { OID rsaEncryption, NULL }
     *     BIT STRING { SEQUENCE { INTEGER modulus, INTEGER exponent } }
     *   }
     *
     * @param array<string, mixed> $jwk
     */
    private function jwkToPem(array $jwk): string
    {
        if (($jwk['kty'] ?? '') !== 'RSA') {
            throw new ApiException('AUTH_INVALID', "Unsupported JWK type '{$jwk['kty']}' (only RSA is supported).", 401);
        }
        $n = isset($jwk['n']) ? self::base64UrlDecode((string)$jwk['n']) : '';
        $e = isset($jwk['e']) ? self::base64UrlDecode((string)$jwk['e']) : '';
        if ($n === '' || $e === '') {
            throw new ApiException('AUTH_INVALID', 'OIDC signing key is missing modulus/exponent.', 401);
        }

        $rsaPubKey = self::derSequence(self::derInteger($n) . self::derInteger($e));
        $algorithmIdentifier = self::derSequence(
            // OID 1.2.840.113549.1.1.1 (rsaEncryption)
            "\x06\x09\x2A\x86\x48\x86\xF7\x0D\x01\x01\x01" .
            "\x05\x00" // NULL parameters
        );
        $bitString = "\x03" . self::derLength(strlen($rsaPubKey) + 1) . "\x00" . $rsaPubKey;
        $spki = self::derSequence($algorithmIdentifier . $bitString);

        return "-----BEGIN PUBLIC KEY-----\n"
            . chunk_split(base64_encode($spki), 64, "\n")
            . "-----END PUBLIC KEY-----\n";
    }

    /** @param array<string, mixed> $payload */
    private function validateClaims(array $payload): void
    {
        $now = time();

        // SECURITY-REVIEW M1: require `exp` — never accept a token that cannot
        // expire. L10: allow a small clock-skew leeway at the boundary.
        $clockSkew = 60;
        if (!isset($payload['exp']) || !is_numeric($payload['exp'])) {
            throw new ApiException('AUTH_INVALID', 'OIDC token is missing the required exp claim.', 401);
        }
        if ($now > (int)$payload['exp'] + $clockSkew) {
            throw new ApiException('AUTH_INVALID', 'OIDC token expired.', 401);
        }
        if (isset($payload['nbf']) && is_numeric($payload['nbf']) && $now + $clockSkew < (int)$payload['nbf']) {
            throw new ApiException('AUTH_INVALID', 'OIDC token not active yet.', 401);
        }

        // SECURITY-REVIEW M2: issuer and audience are MANDATORY when OIDC is
        // enabled. Without an `aud` binding, any token the IdP signed for any
        // application under the same issuer would be accepted (confused-deputy).
        // The boot-time check normally catches this; runtime backstop here.
        $issuer = $this->config['issuer'] ?? null;
        if (!is_string($issuer) || $issuer === '') {
            throw new ApiException('AUTH_CONFIG_ERROR', 'OIDC issuer is not configured.', 500);
        }
        if ((string)($payload['iss'] ?? '') !== $issuer) {
            throw new ApiException('AUTH_INVALID', 'OIDC token issuer mismatch.', 401);
        }

        $audience = $this->config['audience'] ?? null;
        if (!is_string($audience) || $audience === '') {
            throw new ApiException('AUTH_CONFIG_ERROR', 'OIDC audience is not configured.', 500);
        }
        $tokenAud = $payload['aud'] ?? null;
        if (is_string($tokenAud)) {
            if ($tokenAud !== $audience) {
                throw new ApiException('AUTH_INVALID', 'OIDC token audience mismatch.', 401);
            }
        } elseif (is_array($tokenAud)) {
            if (!in_array($audience, $tokenAud, true)) {
                throw new ApiException('AUTH_INVALID', 'OIDC token audience mismatch.', 401);
            }
        } else {
            throw new ApiException('AUTH_INVALID', 'OIDC token audience missing.', 401);
        }
    }

    /**
     * Map IdP-specific claims into Shape's auth-context format
     * (`id`, `email`, `role`) without losing the original claims.
     *
     * @param array<string, mixed> $payload
     * @return array<string, mixed>
     */
    private function mapClaims(array $payload): array
    {
        $idClaim = (string)($this->config['id_claim'] ?? 'sub');
        $emailClaim = (string)($this->config['email_claim'] ?? 'email');
        $roleClaim = (string)($this->config['role_claim'] ?? 'role');
        $mapping = is_array($this->config['role_mapping'] ?? null) ? $this->config['role_mapping'] : [];

        $rawRole = $payload[$roleClaim] ?? null;
        $role = '';
        if (is_string($rawRole)) {
            $role = $mapping[$rawRole] ?? $rawRole;
        } elseif (is_array($rawRole)) {
            // Token has roles[]; the first claim that has a mapping wins.
            // If nothing maps, fall back to the first role string verbatim.
            foreach ($rawRole as $candidate) {
                if (is_string($candidate) && isset($mapping[$candidate])) {
                    $role = (string)$mapping[$candidate];
                    break;
                }
            }
            if ($role === '') {
                foreach ($rawRole as $candidate) {
                    if (is_string($candidate) && $candidate !== '') {
                        $role = $candidate;
                        break;
                    }
                }
            }
        }

        $context = $payload;
        $context['id'] = $payload[$idClaim] ?? $payload['sub'] ?? null;
        $context['email'] = (string)($payload[$emailClaim] ?? $payload['email'] ?? '');
        $context['role'] = (string)$role;
        $context['_oidc'] = true; // Mark the source so app code can branch if needed
        return $context;
    }

    /**
     * Map the verified token's foreign identifier (oid) to the local
     * users.id. Throws AUTH_FORBIDDEN with a clear hint when the user
     * isn't provisioned and `auto_provision` is off.
     *
     * @param array<string, mixed> $context
     * @return array<string, mixed>
     */
    private function resolveLocalUserId(array $context): array
    {
        $usersTable = (string)($this->config['users_table'] ?? 'users');
        $lookupColumn = (string)($this->config['users_lookup_column'] ?? 'oid');
        // The oid for lookup. Prefer 'oid' (Entra-style), fall back to
        // whatever the configured id_claim mapped into context['id'].
        $oid = $context['oid'] ?? $context['sub'] ?? $context['id'] ?? null;
        if (!is_string($oid) || $oid === '') {
            throw new ApiException(
                'AUTH_INVALID',
                'OIDC token has no usable identifier for local user lookup.',
                401,
            );
        }

        // The Database here is shared with the rest of the request — same
        // connection, so a SELECT here is part of the same transaction
        // context if the caller wraps the request in one.
        // SQL Server caps rows with a leading `TOP 1`, not a trailing `LIMIT 1`.
        $isMssql = $this->database->getDriver() === 'sqlsrv';
        // Under tenancy the tenant comes from the LOCAL user row — the IdP
        // token never decides the tenant (MULTI-TENANCY-PLAN §2.3). Scope
        // claims resolve the same way (ROW-SCOPES §2.2).
        $tenancy = $this->tenancyConfig();
        $tenantCol = $tenancy !== null ? $this->tenantColumn($tenancy) : null;
        $tenantSelect = $tenantCol !== null ? ', ' . $this->database->quoteIdentifier($tenantCol) : '';
        $scopeCols = $this->scopeUserColumns();
        foreach ($scopeCols as $col) {
            $tenantSelect .= ', ' . $this->database->quoteIdentifier($col);
        }
        $sql = 'SELECT ' . ($isMssql ? 'TOP 1 ' : '')
            . $this->database->quoteIdentifier('id') . ', '
            . $this->database->quoteIdentifier('email') . ', '
            . $this->database->quoteIdentifier('role')
            . $tenantSelect
            . ' FROM ' . $this->database->quoteIdentifier($usersTable)
            . ' WHERE ' . $this->database->quoteIdentifier($lookupColumn) . ' = :oid'
            . ($isMssql ? '' : ' LIMIT 1');

        $result = $this->database->run($sql, ['oid' => $oid]);
        $row = $result['rows'][0] ?? null;

        if ($row === null) {
            if (($this->config['auto_provision'] ?? false) === true) {
                // Gate the INSERT on the mapped role being one the operator
                // has explicitly declared via role_mapping. Without this gate,
                // a token whose IdP role didn't match any mapping key falls
                // through verbatim (e.g. 'TO.Auditor' from a tenant where
                // only 'TO.Developer' is mapped) and would auto-provision a
                // row that could never pass a mask check — orphan junk in
                // the users table.
                //
                // role_mapping VALUES are the operator's explicit framework-
                // role declaration. When unset, fall back to permissive
                // behavior — that's the contract for setups that intend to
                // accept tokens verbatim.
                $tokenRole = (string)($context['role'] ?? '');
                $effectiveRole = $tokenRole !== ''
                    ? $tokenRole
                    : (string)($this->config['auto_provision_role'] ?? 'user');

                $mappingValues = is_array($this->config['role_mapping'] ?? null)
                    ? array_values(array_unique(array_map(static fn($v): string => (string)$v, $this->config['role_mapping'])))
                    : [];

                if ($mappingValues !== [] && !in_array($effectiveRole, $mappingValues, true)) {
                    throw new ApiException(
                        'AUTH_FORBIDDEN',
                        "OIDC user has unmapped role '{$effectiveRole}'; nothing was provisioned.",
                        403,
                        null,
                        [
                            'expected' => 'token role mapped to one of: ' . implode(', ', $mappingValues),
                            'hint' => "Token's role claim resolved to '{$effectiveRole}', which isn't in auth.oidc.role_mapping values. Either map this IdP role explicitly (add an entry to role_mapping), remove the user from the unmapped IdP role, or — if you intend to accept the IdP's role string verbatim — clear role_mapping to opt out of the framework-role gate.",
                        ]
                    );
                }

                return $this->autoProvision($context, $oid, $usersTable, $lookupColumn);
            }
            throw new ApiException(
                'AUTH_FORBIDDEN',
                "OIDC user '{$oid}' is not provisioned in '{$usersTable}'.",
                403,
                null,
                [
                    'expected' => "Row in '{$usersTable}' where '{$lookupColumn}' = '{$oid}'",
                    'hint' => "Insert a row into '{$usersTable}' with {$lookupColumn} set to the token's oid, or set 'auth.oidc.auto_provision' => true to create rows on first login.",
                ]
            );
        }

        // Overwrite id with the local users.id (int PK). Preserve oid for
        // app code that wants the IdP identifier. Keep token-mapped role
        // as authoritative (the IdP's role-assignment decision wins by
        // default; a future role_source: 'local' switch can flip this).
        $context['id'] = is_int($row[0]) ? $row[0] : (ctype_digit((string)$row[0]) ? (int)$row[0] : $row[0]);
        $context['oid'] = $oid;
        $localEmail = self::stringOrNull($row[1] ?? null);
        if ($localEmail !== null) {
            $context['email'] = $localEmail;  // local table is source of truth for email
        }
        $localRole = self::stringOrNull($row[2] ?? null) ?? '';
        if ($tenancy !== null) {
            $rowTenant = $row[3] ?? null;
            $unscoped = $this->isUnscopedRole($tenancy, $localRole);
            // Unscoped roles (superadmin) stay reachable even when their home
            // tenant is suspended — otherwise the account that can re-activate
            // a tenant is locked out with it. Mirrors LoginService::login().
            if (!$unscoped) {
                $this->ensureTenantActive($tenancy, $rowTenant);
            }
            $context['tenant_id'] = $unscoped ? null : $rowTenant;
        }

        // Dimension scope claims from the local row / claim_table (§2.2) —
        // never from the IdP token. Roles unscoped for a dimension get null.
        $scopeBase = 3 + ($tenantCol !== null ? 1 : 0);
        $rowValues = [];
        foreach ($scopeCols as $i => $col) {
            $rowValues[$col] = $row[$scopeBase + $i] ?? null;
        }
        foreach ($this->scopesConfig() as $column => $spec) {
            $claim = isset($spec['claim']) && is_string($spec['claim']) && $spec['claim'] !== '' ? $spec['claim'] : (string)$column;
            if ($this->isUnscopedForScope($spec, $localRole)) {
                $context[$claim] = null;
                continue;
            }
            $claimTable = is_array($spec['claim_table'] ?? null) ? $spec['claim_table'] : null;
            if ($claimTable !== null) {
                $context[$claim] = $this->fetchScopeMemberships($claimTable, (string)$column, $context['id']);
                continue;
            }
            $value = ($spec['self'] ?? false) === true ? $context['id'] : ($rowValues[(string)$column] ?? null);
            $value = $value !== null && $value !== '' ? (is_numeric($value) ? (int)$value : (string)$value) : null;
            $context[$claim] = ($spec['multi'] ?? false) ? ($value === null ? [] : [$value]) : $value;
        }

        $context['_local_user'] = [
            'id' => $context['id'],
            'email' => $localEmail,
            'role' => self::stringOrNull($row[2] ?? null),
        ];
        if ($tenancy !== null) {
            $context['_local_user']['tenant_id'] = $context['tenant_id'];
        }

        return $context;
    }

    /**
     * Create a row in the users table for an OIDC-authenticated caller
     * we've never seen before. Opt-in via `auto_provision` config.
     *
     * Role precedence on auto-provision:
     *   1. The token's mapped role (already in $context['role'] after
     *      mapClaims() applied role_claim + role_mapping). The IdP's
     *      role assignment is the source of truth.
     *   2. `auto_provision_role` config (default 'user') as a fallback
     *      when the token has no role claim or no mapping matched.
     *
     * Without precedence rule 1, an Entra `BackendAdmin` user would silently
     * land with local `role: 'user'` on first login, since
     * auto_provision_role defaulted to 'user' regardless of what the token
     * carried.
     *
     * @param array<string, mixed> $context
     * @return array<string, mixed>
     */
    private function autoProvision(array $context, string $oid, string $usersTable, string $lookupColumn): array
    {
        $email = (string)($context['email'] ?? '');

        $tokenRole = (string)($context['role'] ?? '');
        $role = $tokenRole !== ''
            ? $tokenRole
            : (string)($this->config['auto_provision_role'] ?? 'user');

        // password_hash is non-null on most schemas — store a value that
        // can never validate via password_verify so this row can't be used
        // for the local password-login flow. The user only authenticates
        // via OIDC.
        $sentinel = '!locked-oidc-only!';

        $data = [
            $lookupColumn => $oid,
            'email' => $email,
            'password_hash' => $sentinel,
            'role' => $role,
        ];

        // Under tenancy the provisioned row needs a home tenant (§2.3):
        // an IdP claim (tenancy.oidc_tenant_claim) or the configured fixed
        // default (tenancy.oidc_auto_provision_tenant). Never guess.
        $tenancy = $this->tenancyConfig();
        $tenantId = null;
        if ($tenancy !== null) {
            $tenantId = $this->resolveProvisionTenant($tenancy, $context);
            $tenantCol = $this->tenantColumn($tenancy);
            $data[$tenantCol] = $tenantId;
            $this->ensureTenantActive($tenancy, $tenantId);
        }

        // Single-valued scope columns need a value too (ROW-SCOPES §2.2):
        // the configured scopes.<col>.oidc_auto_provision_value — never
        // guessed. claim_table scopes have no users column; memberships
        // start empty.
        $scopeValues = [];
        foreach ($this->scopesConfig() as $column => $spec) {
            if (($spec['claim_table'] ?? null) !== null || ($spec['self'] ?? false) === true) {
                continue;
            }
            $value = $spec['oidc_auto_provision_value'] ?? null;
            if (!is_int($value) && (!is_string($value) || $value === '')) {
                throw new ApiException(
                    'AUTH_CONFIG_ERROR',
                    "OIDC auto-provision is missing a value for scope '{$column}'.",
                    500,
                    null,
                    ['hint' => "Configure scopes.{$column}.oidc_auto_provision_value (the value stamped on auto-provisioned users), or disable auth.oidc.auto_provision."]
                );
            }
            $data[(string)$column] = $value;
            $scopeValues[(string)$column] = is_numeric($value) ? (int)$value : $value;
        }
        $cols = array_keys($data);

        // Legacy users tables with a plain int PK (no identity) need the
        // framework's MAX+1 allocation. Auth tables are never introspected,
        // so only an explicit 'manual' in the pk_assignment map (injected
        // into auth.oidc config by ShapeApp) activates this path.
        $pkMode = Database::resolvePkAssignmentMode($this->config['pk_assignment'] ?? null, $usersTable);
        if ($pkMode === 'manual') {
            $newId = $this->database->insertWithManualPk($usersTable, 'id', $data);
        } else {
            $quoted = array_map(fn(string $c): string => $this->database->quoteIdentifier($c), $cols);
            $placeholders = array_map(fn(string $c): string => ':' . $c, $cols);

            $sql = 'INSERT INTO ' . $this->database->quoteIdentifier($usersTable)
                . ' (' . implode(', ', $quoted) . ')'
                . ' VALUES (' . implode(', ', $placeholders) . ')';

            $newId = $this->database->insertReturningId($sql, $data);
        }

        $context['id'] = $newId;
        $context['oid'] = $oid;
        $context['email'] = $email;
        if ($tenancy !== null) {
            $context['tenant_id'] = $this->isUnscopedRole($tenancy, $role) ? null : $tenantId;
        }
        foreach ($this->scopesConfig() as $column => $spec) {
            $claim = isset($spec['claim']) && is_string($spec['claim']) && $spec['claim'] !== '' ? $spec['claim'] : (string)$column;
            if ($this->isUnscopedForScope($spec, $role)) {
                $context[$claim] = null;
                continue;
            }
            if (($spec['claim_table'] ?? null) !== null) {
                $context[$claim] = []; // freshly provisioned — no memberships yet
                continue;
            }
            $stamped = ($spec['self'] ?? false) === true ? $newId : ($scopeValues[(string)$column] ?? null);
            $context[$claim] = ($spec['multi'] ?? false) ? ($stamped === null ? [] : [$stamped]) : $stamped;
        }
        $context['_local_user'] = [
            'id' => $newId,
            'email' => $email,
            'role' => $role,
            'auto_provisioned' => true,
        ];
        if ($tenancy !== null) {
            $context['_local_user']['tenant_id'] = $context['tenant_id'];
        }

        return $context;
    }

    // ── Tenancy helpers (MULTI-TENANCY-PLAN §2.3) ─────────────────────

    /**
     * The tenancy config handed down by JwtGuard (originates in ShapeApp's
     * normalized `tenancy` block); null when tenancy is off.
     *
     * @return array<string, mixed>|null
     */
    private function tenancyConfig(): ?array
    {
        $tenancy = $this->config['tenancy'] ?? null;
        return is_array($tenancy) && $tenancy !== [] ? $tenancy : null;
    }

    /** @param array<string, mixed> $tenancy */
    private function isUnscopedRole(array $tenancy, string $role): bool
    {
        $unscoped = $tenancy['unscoped_roles'] ?? [];
        return $role !== '' && is_array($unscoped) && in_array($role, $unscoped, true);
    }

    // ── Row-scope helpers (ROW-SCOPES-PHP-PLAN §2.2) ──────────────────

    /**
     * Normalized `scopes` config handed down by JwtGuard; [] when off.
     *
     * @return array<string, array<string, mixed>>
     */
    private function scopesConfig(): array
    {
        $scopes = $this->config['scopes'] ?? null;
        return is_array($scopes) ? array_filter($scopes, 'is_array') : [];
    }

    /**
     * Scope columns read straight off the users row (claim_table scopes read
     * a membership table instead). Operator config interpolated into SQL —
     * same identifier rule as tenantColumn().
     *
     * @return list<string>
     */
    private function scopeUserColumns(): array
    {
        $cols = [];
        foreach ($this->scopesConfig() as $column => $spec) {
            if (($spec['claim_table'] ?? null) !== null || ($spec['self'] ?? false) === true) {
                continue;
            }
            $col = (string)$column;
            if (!preg_match('/^[A-Za-z_][A-Za-z0-9_]*$/', $col)) {
                throw new ApiException('AUTH_CONFIG_ERROR', 'Invalid scope column name.', 500);
            }
            $cols[] = $col;
        }
        return $cols;
    }

    /** @param array<string, mixed> $spec */
    private function isUnscopedForScope(array $spec, string $role): bool
    {
        $unscoped = $spec['unscoped_roles'] ?? [];
        return $role !== '' && is_array($unscoped) && in_array($role, $unscoped, true);
    }

    /**
     * Multi-membership claim source (§2.2): one indexed SELECT against the
     * configured claim_table, returning the caller's value list.
     *
     * @param array<string, mixed> $claimTable ['table' => ..., 'user_column' => ...]
     * @return list<int|string>
     */
    private function fetchScopeMemberships(array $claimTable, string $column, mixed $userId): array
    {
        $table = (string)($claimTable['table'] ?? '');
        $userColumn = (string)($claimTable['user_column'] ?? 'user_id');
        $ident = '/^[A-Za-z_][A-Za-z0-9_]*$/';
        if (!preg_match($ident, $table) || !preg_match($ident, $userColumn) || !preg_match($ident, $column)) {
            throw new ApiException('AUTH_CONFIG_ERROR', 'Invalid scope claim_table configuration.', 500);
        }

        $sql = 'SELECT ' . $this->database->quoteIdentifier($column)
            . ' FROM ' . $this->database->quoteIdentifier($table)
            . ' WHERE ' . $this->database->quoteIdentifier($userColumn) . ' = :uid';
        $result = $this->database->run($sql, ['uid' => $userId]);

        $values = [];
        foreach ($result['rows'] as $row) {
            $v = $row[0] ?? null;
            if ($v !== null && $v !== '') {
                $values[] = is_numeric($v) ? (int)$v : (string)$v;
            }
        }
        return $values;
    }

    /** @param array<string, mixed> $tenancy */
    private function tenantColumn(array $tenancy): string
    {
        $col = isset($tenancy['column']) && is_string($tenancy['column']) && $tenancy['column'] !== ''
            ? $tenancy['column']
            : 'tenant_id';
        if (!preg_match('/^[A-Za-z_][A-Za-z0-9_]*$/', $col)) {
            throw new ApiException('AUTH_CONFIG_ERROR', 'Invalid tenancy column name.', 500);
        }
        return $col;
    }

    /**
     * Tenant to stamp on an auto-provisioned row: the configured IdP claim
     * wins, then the fixed default. Neither present → config error (the
     * build-time check catches this first; this is the runtime backstop).
     *
     * @param array<string, mixed> $tenancy
     * @param array<string, mixed> $context
     */
    private function resolveProvisionTenant(array $tenancy, array $context): int|string
    {
        $claim = $tenancy['oidc_tenant_claim'] ?? null;
        if (is_string($claim) && $claim !== '' && isset($context[$claim]) && $context[$claim] !== '') {
            $value = $context[$claim];
            if (is_int($value) || is_string($value)) {
                return is_numeric($value) ? (int)$value : $value;
            }
        }

        $fixed = $tenancy['oidc_auto_provision_tenant'] ?? null;
        if (is_int($fixed) || (is_string($fixed) && $fixed !== '')) {
            return $fixed;
        }

        throw new ApiException(
            'AUTH_CONFIG_ERROR',
            'OIDC auto-provision is missing a tenant source.',
            500,
            null,
            ['hint' => 'Configure tenancy.oidc_tenant_claim or tenancy.oidc_auto_provision_tenant.']
        );
    }

    /**
     * Reject when the tenant registry row is missing / suspended / removed —
     * the OIDC path verifies per request, so this doubles as the immediate
     * suspension gate for OIDC users. Mirrors LoginService::assertTenantActive.
     *
     * @param array<string, mixed> $tenancy
     */
    private function ensureTenantActive(array $tenancy, mixed $tenantId): void
    {
        if ($tenantId === null || $tenantId === '') {
            throw new ApiException('TENANT_SUSPENDED', 'Your account is not assigned to an active tenant.', 403);
        }
        $table = isset($tenancy['table']) && is_string($tenancy['table']) && $tenancy['table'] !== ''
            ? $tenancy['table']
            : 'tenants';
        if (!preg_match('/^[A-Za-z_][A-Za-z0-9_]*$/', $table)) {
            throw new ApiException('AUTH_CONFIG_ERROR', 'Invalid tenancy registry table name.', 500);
        }

        $isMssql = $this->database !== null && $this->database->getDriver() === 'sqlsrv';
        $sql = $isMssql
            ? "SELECT TOP 1 * FROM {$table} WHERE id = :tenant_id"
            : "SELECT * FROM {$table} WHERE id = :tenant_id LIMIT 1";
        $result = $this->database->run($sql, ['tenant_id' => $tenantId]);

        $rows = $result['rows'];
        if ($rows === []) {
            throw new ApiException('TENANT_SUSPENDED', 'Your tenant no longer exists.', 403);
        }
        $columns = $result['columns'];
        $row = count($columns) === count($rows[0]) ? array_combine($columns, $rows[0]) : [];

        if (array_key_exists('is_active', $row) && (int)$row['is_active'] !== 1) {
            throw new ApiException('TENANT_SUSPENDED', 'Your tenant has been suspended.', 403);
        }
        if (array_key_exists('deleted_at', $row) && $row['deleted_at'] !== null && $row['deleted_at'] !== '') {
            throw new ApiException('TENANT_SUSPENDED', 'Your tenant has been removed.', 403);
        }
    }

    // ── DER / base64url helpers ───────────────────────────────────────

    private static function derSequence(string $contents): string
    {
        return "\x30" . self::derLength(strlen($contents)) . $contents;
    }

    private static function derInteger(string $bytes): string
    {
        // ASN.1 INTEGER is signed; if the high bit of the first byte is set,
        // we'd otherwise be encoding a negative number. Prepend 0x00 so the
        // value stays positive.
        if ($bytes !== '' && (ord($bytes[0]) & 0x80) !== 0) {
            $bytes = "\x00" . $bytes;
        }
        return "\x02" . self::derLength(strlen($bytes)) . $bytes;
    }

    private static function derLength(int $len): string
    {
        if ($len < 0x80) {
            return chr($len);
        }
        $bytes = '';
        while ($len > 0) {
            $bytes = chr($len & 0xFF) . $bytes;
            $len >>= 8;
        }
        return chr(0x80 | strlen($bytes)) . $bytes;
    }

    private static function base64UrlDecode(string $raw): string
    {
        $pad = strlen($raw) % 4;
        if ($pad > 0) {
            $raw .= str_repeat('=', 4 - $pad);
        }
        $decoded = base64_decode(strtr($raw, '-_', '+/'), true);
        return is_string($decoded) ? $decoded : '';
    }

    private static function stringOrNull(mixed $v): ?string
    {
        if ($v === null) {
            return null;
        }
        if (is_scalar($v)) {
            $s = (string)$v;
            return $s === '' ? null : $s;
        }
        return null;
    }
}
