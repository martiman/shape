<?php

declare(strict_types=1);

namespace ShapeRuntime\Auth;

use ShapeRuntime\Core\ApiException;
use ShapeRuntime\Core\Database;

final class JwtGuard implements AuthGuard
{
    /**
     * @param array<string, mixed> $config
     * @param ?Database $database Optional. When present, OIDC tokens get
     *   their `oid` resolved to a local users.id via the OidcVerifier
     *   (so `created_by` / audit_log.actor_id always reference users.id
     *   regardless of auth path). Without it, the OIDC path's `id` stays
     *   as the IdP's claim value.
     */
    public function __construct(
        private readonly array $config,
        private readonly ?Database $database = null,
    ) {
    }

    public function authorize(array $headers): array
    {
        $auth = $headers['authorization'] ?? '';
        if (!is_string($auth) || stripos($auth, 'bearer ') !== 0) {
            throw new ApiException('AUTH_REQUIRED', 'Missing Bearer token.', 401);
        }

        $token = trim(substr($auth, 7));
        if ($token === '') {
            throw new ApiException('AUTH_REQUIRED', 'Missing Bearer token.', 401);
        }

        $parts = explode('.', $token);
        if (count($parts) !== 3) {
            throw new ApiException('AUTH_INVALID', 'Invalid JWT format.', 401);
        }

        [$h64, $p64, $s64] = $parts;
        $header = $this->decodeJsonBase64Url($h64);
        $payload = $this->decodeJsonBase64Url($p64);
        $alg = (string)($header['alg'] ?? '');

        // Optional OIDC path: when `auth.oidc` is configured, RS256/RS384/
        // RS512 tokens are validated against the configured JWKS endpoint.
        // The HS256 path below is unchanged — local Shape-issued tokens
        // (auth.token / auth.refresh) keep working alongside.
        $oidcConfig = is_array($this->config['oidc'] ?? null) ? $this->config['oidc'] : null;
        if ($oidcConfig !== null) {
            // The verifier resolves the tenant + scope claims from the LOCAL
            // user row after oid → users.id resolution (§2.3, ROW-SCOPES
            // §2.2) — hand it the tenancy + scopes config.
            if (is_array($this->config['tenancy'] ?? null) && !isset($oidcConfig['tenancy'])) {
                $oidcConfig['tenancy'] = $this->config['tenancy'];
            }
            if (is_array($this->config['scopes'] ?? null) && !isset($oidcConfig['scopes'])) {
                $oidcConfig['scopes'] = $this->config['scopes'];
            }
            $verifier = new OidcVerifier($oidcConfig, $this->database);
            if ($verifier->handlesAlgorithm($alg)) {
                $signingInput = $h64 . '.' . $p64;
                $signature = $this->base64UrlDecode($s64);
                return $verifier->verify($signingInput, $signature, $header, $payload);
            }
        }

        // HS256 path — Shape-issued tokens, primary auth via local users table.
        if ($alg !== 'HS256') {
            throw new ApiException('AUTH_INVALID', 'Unsupported JWT algorithm.', 401);
        }

        $secret = (string)($this->config['jwt_secret'] ?? '');
        if ($secret === '') {
            throw new ApiException('AUTH_INVALID', 'JWT secret is not configured.', 500);
        }

        $sig = $this->base64UrlDecode($s64);
        $signed = hash_hmac('sha256', $h64 . '.' . $p64, $secret, true);
        if (!hash_equals($signed, $sig)) {
            throw new ApiException('AUTH_INVALID', 'JWT signature mismatch.', 401);
        }

        $now = time();
        // SECURITY-REVIEW M1: require `exp`. A token with no expiry claim would
        // otherwise be accepted forever. Shape's own JwtIssuer always sets it.
        if (!isset($payload['exp']) || !is_numeric($payload['exp'])) {
            throw new ApiException('AUTH_INVALID', 'JWT is missing the required exp claim.', 401);
        }
        // SECURITY-REVIEW L10: small clock-skew leeway so a valid token isn't
        // rejected at the boundary across hosts with drifting clocks.
        $clockSkew = 60;
        if ($now > (int)$payload['exp'] + $clockSkew) {
            throw new ApiException('AUTH_INVALID', 'JWT expired.', 401);
        }
        if (isset($payload['nbf']) && is_numeric($payload['nbf']) && $now + $clockSkew < (int)$payload['nbf']) {
            throw new ApiException('AUTH_INVALID', 'JWT not active yet.', 401);
        }

        $issuer = $this->config['jwt_issuer'] ?? null;
        if (is_string($issuer) && $issuer !== '' && (($payload['iss'] ?? null) !== $issuer)) {
            throw new ApiException('AUTH_INVALID', 'JWT issuer mismatch.', 401);
        }

        $aud = $this->config['jwt_audience'] ?? null;
        if (is_string($aud) && $aud !== '') {
            $pAud = $payload['aud'] ?? null;
            if (is_string($pAud) && $pAud !== $aud) {
                throw new ApiException('AUTH_INVALID', 'JWT audience mismatch.', 401);
            }
            if (is_array($pAud) && !in_array($aud, $pAud, true)) {
                throw new ApiException('AUTH_INVALID', 'JWT audience mismatch.', 401);
            }
            if (!is_string($pAud) && !is_array($pAud)) {
                throw new ApiException('AUTH_INVALID', 'JWT audience mismatch.', 401);
            }
        }

        return $this->normalizeScoping($payload);
    }

    /**
     * Per-scope claim normalization (MULTI-TENANCY-PLAN §2.3, ROW-SCOPES
     * §2.2), mirroring .NET JwtGuard.NormalizeTenancy generalized to N
     * ScopeSpecs: a role unscoped for a scope carries that claim = null; a
     * scoped caller must present the claim — pre-feature tokens are rejected
     * with a re-login hint, never falling through to unscoped. With
     * `check_active_per_request` on and a Database wired, the tenant
     * registry row is verified per request so suspension is immediate
     * instead of lagging one access-token TTL (§2.3 timing). Dimensions have
     * no registry — nothing to suspend.
     *
     * @param array<string, mixed> $payload
     * @return array<string, mixed>
     */
    private function normalizeScoping(array $payload): array
    {
        $role = isset($payload['role']) ? (string)$payload['role'] : '';

        $tenancy = $this->config['tenancy'] ?? null;
        if (!is_array($tenancy) || $tenancy === []) {
            $tenancy = null;
        }
        if ($tenancy !== null) {
            $unscopedRoles = is_array($tenancy['unscoped_roles'] ?? null) ? $tenancy['unscoped_roles'] : [];
            if ($role !== '' && in_array($role, $unscopedRoles, true)) {
                $payload['tenant_id'] = null;
            } else {
                $tenantId = $payload['tenant_id'] ?? null;
                if ($tenantId === null || $tenantId === '') {
                    throw new ApiException(
                        'AUTH_INVALID',
                        'Missing tenant_id claim. Re-login required after tenancy was enabled.',
                        401,
                        null,
                        ['hint' => 'Re-authenticate — access tokens issued before tenancy was enabled lack the tenant claim.']
                    );
                }
                if ($this->database !== null && ($tenancy['check_active_per_request'] ?? false) === true) {
                    $this->ensureTenantActive($tenancy, $tenantId);
                }
            }
        }

        $scopes = is_array($this->config['scopes'] ?? null) ? $this->config['scopes'] : [];
        foreach ($scopes as $column => $spec) {
            if (!is_array($spec)) {
                continue;
            }
            $claim = isset($spec['claim']) && is_string($spec['claim']) && $spec['claim'] !== '' ? $spec['claim'] : (string)$column;
            $unscoped = is_array($spec['unscoped_roles'] ?? null) ? $spec['unscoped_roles'] : [];
            if ($role !== '' && in_array($role, $unscoped, true)) {
                $payload[$claim] = null;
                continue;
            }
            $value = $payload[$claim] ?? null;
            if ($value === null || $value === '') {
                throw new ApiException(
                    'AUTH_INVALID',
                    "Missing {$claim} claim. Re-login required after row scopes were enabled.",
                    401,
                    null,
                    ['hint' => 'Re-authenticate — access tokens issued before row scopes were enabled lack the scope claim.']
                );
            }
        }

        return $payload;
    }

    /**
     * Per-request suspension gate: one indexed SELECT against the registry,
     * inspecting the returned columns for the `is_active` / `deleted_at`
     * conventions (no separate introspection round-trip on the hot auth
     * path — mirrors .NET TenancyResolver.EnsureTenantActive).
     *
     * @param array<string, mixed> $tenancy
     */
    private function ensureTenantActive(array $tenancy, mixed $tenantId): void
    {
        assert($this->database !== null);
        $table = isset($tenancy['table']) && is_string($tenancy['table']) && $tenancy['table'] !== ''
            ? $tenancy['table']
            : 'tenants';
        if (!preg_match('/^[A-Za-z_][A-Za-z0-9_]*$/', $table)) {
            throw new ApiException('AUTH_CONFIG_ERROR', 'Invalid tenancy registry table name.', 500);
        }

        $driver = $this->database->getDriver();
        $sql = $driver === 'sqlsrv'
            ? "SELECT TOP 1 * FROM {$table} WHERE id = :tenant_id"
            : "SELECT * FROM {$table} WHERE id = :tenant_id LIMIT 1";
        $result = $this->database->run($sql, ['tenant_id' => $tenantId]);

        $rows = $result['rows'];
        if ($rows === []) {
            throw self::tenantSuspended('Your tenant no longer exists.');
        }
        $columns = $result['columns'];
        $row = count($columns) === count($rows[0]) ? array_combine($columns, $rows[0]) : [];

        if (array_key_exists('is_active', $row) && (int)$row['is_active'] !== 1) {
            throw self::tenantSuspended('Your tenant has been suspended.');
        }
        if (array_key_exists('deleted_at', $row) && $row['deleted_at'] !== null && $row['deleted_at'] !== '') {
            throw self::tenantSuspended('Your tenant has been removed.');
        }
    }

    private static function tenantSuspended(string $message): ApiException
    {
        return new ApiException('TENANT_SUSPENDED', $message, 403, null, [
            'hint' => 'Reactivate the tenant or select a different tenant before retrying.',
        ]);
    }

    /** @return array<string, mixed> */
    private function decodeJsonBase64Url(string $raw): array
    {
        $decoded = $this->base64UrlDecode($raw);
        $arr = json_decode($decoded, true);
        if (!is_array($arr)) {
            throw new ApiException('AUTH_INVALID', 'Invalid JWT payload.', 401);
        }
        return $arr;
    }

    private function base64UrlDecode(string $raw): string
    {
        $pad = strlen($raw) % 4;
        if ($pad > 0) {
            $raw .= str_repeat('=', 4 - $pad);
        }
        $decoded = base64_decode(strtr($raw, '-_', '+/'), true);
        if (!is_string($decoded)) {
            throw new ApiException('AUTH_INVALID', 'Invalid JWT encoding.', 401);
        }
        return $decoded;
    }
}
