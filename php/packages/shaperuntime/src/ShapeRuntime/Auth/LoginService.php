<?php

declare(strict_types=1);

namespace ShapeRuntime\Auth;

use PDO;
use PDOException;
use Exception;
use ShapeRuntime\Core\ApiException;
use ShapeRuntime\Core\Database;

final class LoginService
{
    /**
     * A valid BCrypt hash used only to spend equivalent CPU on the
     * user-not-found login branch, closing the enumeration timing oracle
     * (SECURITY-REVIEW M4). Never matches a real password.
     */
    private const DUMMY_BCRYPT_HASH = '$2y$12$lwMo6btJB5s9xhD34abgMO19WGG6eFqLF0cHyw04qZLWGK8kU3g5K';

    /**
     * @param array<string, mixed> $dbConfig
     * @param array<string, mixed> $authConfig
     */
    public function __construct(
        private readonly array $dbConfig,
        private readonly array $authConfig,
        private readonly JwtIssuer $issuer
    ) {
    }

    /**
     * @param array<string, mixed> $payload
     * @return array<string, mixed>
     */
    public function login(array $payload): array
    {
        $this->ensureLocalLoginEnabled();

        $email = isset($payload['email']) ? trim((string)$payload['email']) : '';
        $password = isset($payload['password']) ? (string)$payload['password'] : '';

        if ($email === '' || $password === '') {
            throw new ApiException('AUTH_VALIDATION_ERROR', 'Fields email and password are required.', 400);
        }

        $row = $this->findUserByEmail($email);
        if ($row === null) {
            // SECURITY-REVIEW M4: spend equivalent CPU on the not-found branch so
            // its response time can't be used to enumerate valid emails.
            password_verify($password, self::DUMMY_BCRYPT_HASH);
            throw new ApiException('AUTH_INVALID_CREDENTIALS', 'Invalid email or password.', 401);
        }

        $hash = isset($row['password_hash']) ? (string)$row['password_hash'] : '';
        if ($hash === '' || !password_verify($password, $hash)) {
            throw new ApiException('AUTH_INVALID_CREDENTIALS', 'Invalid email or password.', 401);
        }

        $isActive = (int)($row['is_active'] ?? 1);
        if ($isActive !== 1) {
            throw new ApiException('AUTH_INVALID_CREDENTIALS', 'Invalid email or password.', 401);
        }

        $ttl = (int)($this->authConfig['access_token_ttl'] ?? 3600);
        if ($ttl < 60) {
            $ttl = 60;
        }

        $role = (string)($row['role'] ?? 'user');
        $displayName = (string)($row['display_name'] ?? '');
        $userId = (int)($row['id'] ?? 0);

        if ($userId <= 0) {
            throw new ApiException('AUTH_CONFIG_ERROR', 'Invalid users table data.', 500);
        }

        // Tenancy: reject a login against a suspended / removed tenant, and
        // let the tenant_id ride into the token via buildTokenResponse (§2.3).
        $tenantCol = $this->tenantColumn();
        if ($tenantCol !== null && !$this->isUnscopedRole($role)) {
            $tenantId = isset($row[$tenantCol]) ? $this->normalizeTenantId($row[$tenantCol]) : null;
            $this->assertTenantActive($tenantId);
        }

        return $this->issueTokenPair($row);
    }

    /**
     * @param array<string, mixed> $payload
     * @return array<string, mixed>
     */
    public function refresh(array $payload): array
    {
        $refreshToken = isset($payload['refresh_token']) ? trim((string)$payload['refresh_token']) : '';
        if ($refreshToken === '') {
            throw new ApiException('AUTH_VALIDATION_ERROR', 'Field refresh_token is required.', 400);
        }

        $pdo = $this->newPdo();
        $table = $this->refreshTokensTable();
        $hash = hash('sha256', $refreshToken);

        $driver = (string)$pdo->getAttribute(PDO::ATTR_DRIVER_NAME);
        // Re-read the user's tenant + scope columns on rotation so suspension
        // and scope changes propagate at most one access-token TTL later
        // (§2.3, ROW-SCOPES §2.2).
        $tenantCol = $this->tenantColumn();
        $uTenant = $tenantCol !== null ? ", u.{$tenantCol}" : '';
        foreach ($this->scopeUserColumns() as $col) {
            if ($col !== $tenantCol) {
                $uTenant .= ", u.{$col}";
            }
        }

        try {
            $pdo->beginTransaction();

            // SQL Server has no `LIMIT`/`FOR UPDATE`: cap with `TOP 1` and take
            // the row lock via the `WITH (UPDLOCK, ROWLOCK)` table hint.
            // SQLite has no row locks at all (the write transaction locks the
            // whole database) — `FOR UPDATE` is a syntax error there.
            $sql = match (true) {
                $driver === 'sqlsrv' =>
                    "SELECT TOP 1 rt.id, rt.user_id, rt.expires_at, u.email, u.display_name, u.role, u.is_active{$uTenant}
                    FROM {$table} rt WITH (UPDLOCK, ROWLOCK)
                    INNER JOIN {$this->usersTable()} u ON u.id = rt.user_id
                    WHERE rt.token_hash = :token_hash",
                $driver === 'sqlite' =>
                    "SELECT rt.id, rt.user_id, rt.expires_at, u.email, u.display_name, u.role, u.is_active{$uTenant}
                    FROM {$table} rt
                    INNER JOIN {$this->usersTable()} u ON u.id = rt.user_id
                    WHERE rt.token_hash = :token_hash
                    LIMIT 1",
                default =>
                    "SELECT rt.id, rt.user_id, rt.expires_at, u.email, u.display_name, u.role, u.is_active{$uTenant}
                    FROM {$table} rt
                    INNER JOIN {$this->usersTable()} u ON u.id = rt.user_id
                    WHERE rt.token_hash = :token_hash
                    LIMIT 1
                    FOR UPDATE",
            };
            $stmt = $pdo->prepare($sql);
            $stmt->bindValue(':token_hash', $hash);
            $stmt->execute();
            $row = $stmt->fetch(PDO::FETCH_ASSOC);

            if (!is_array($row)) {
                $pdo->rollBack();
                throw new ApiException('AUTH_INVALID_REFRESH_TOKEN', 'Invalid refresh token.', 401);
            }

            $tokenId = (int)($row['id'] ?? 0);
            $userId = (int)($row['user_id'] ?? 0);
            $expiresAt = (string)($row['expires_at'] ?? '');
            $isActive = (int)($row['is_active'] ?? 0);

            if ($tokenId <= 0 || $userId <= 0 || $expiresAt === '' || $isActive !== 1) {
                $pdo->rollBack();
                throw new ApiException('AUTH_INVALID_REFRESH_TOKEN', 'Invalid refresh token.', 401);
            }

            if (strtotime($expiresAt) <= time()) {
                $pdo->rollBack();
                throw new ApiException('AUTH_INVALID_REFRESH_TOKEN', 'Refresh token expired.', 401);
            }

            $revocationSql = $driver === 'sqlsrv'
                ? "SELECT TOP 1 revoked_at FROM {$table} WHERE id = :id"
                : "SELECT revoked_at FROM {$table} WHERE id = :id LIMIT 1";
            $revocationCheck = $pdo->prepare($revocationSql);
            $revocationCheck->bindValue(':id', $tokenId);
            $revocationCheck->execute();
            $revocationRow = $revocationCheck->fetch(PDO::FETCH_ASSOC);
            if (!is_array($revocationRow) || ($revocationRow['revoked_at'] ?? null) !== null) {
                $pdo->rollBack();
                throw new ApiException('AUTH_INVALID_REFRESH_TOKEN', 'Refresh token already used or revoked.', 401);
            }

            // Tenancy: a suspended tenant blocks rotation (rolls back — no new
            // token issued to a suspended tenant's user). Unscoped roles
            // (superadmin) are exempt so they can still recover a suspended tenant.
            if ($tenantCol !== null && !$this->isUnscopedRole((string)($row['role'] ?? 'user'))) {
                $tenantId = isset($row[$tenantCol]) ? $this->normalizeTenantId($row[$tenantCol]) : null;
                $this->assertTenantActive($tenantId);
            }

            $newRefreshToken = $this->generateRefreshToken();
            $newRefreshHash = hash('sha256', $newRefreshToken);
            $refreshTtl = $this->refreshTokenTtl();

            $newTokenId = $this->insertAuthRow($pdo, $table, [
                'user_id' => $userId,
                'token_hash' => $newRefreshHash,
                'expires_at' => gmdate('Y-m-d H:i:s', time() + $refreshTtl),
            ]);

            $update = $pdo->prepare(
                "UPDATE {$table}
                 SET revoked_at = CURRENT_TIMESTAMP,
                     replaced_by_token_id = :new_token_id,
                     last_used_at = CURRENT_TIMESTAMP
                 WHERE id = :id"
            );
            $update->bindValue(':new_token_id', $newTokenId);
            $update->bindValue(':id', $tokenId);
            $update->execute();

            $pdo->commit();

            $userForToken = [
                'id' => $userId,
                'email' => (string)($row['email'] ?? ''),
                'display_name' => (string)($row['display_name'] ?? ''),
                'role' => (string)($row['role'] ?? 'user'),
            ];
            if ($tenantCol !== null) {
                $userForToken[$tenantCol] = $row[$tenantCol] ?? null;
            }
            foreach ($this->scopeUserColumns() as $col) {
                $userForToken[$col] = $row[$col] ?? null;
            }

            return $this->buildTokenResponse($userForToken, $newRefreshToken, $refreshTtl);
        } catch (ApiException $e) {
            if ($pdo->inTransaction()) {
                $pdo->rollBack();
            }
            throw $e;
        } catch (PDOException | Exception $e) {
            if ($pdo->inTransaction()) {
                $pdo->rollBack();
            }
            throw new ApiException('DB_ERROR', 'Database query failed.', 500);
        }
    }

    /**
     * @param array<string, mixed> $payload
     * @return array<string, mixed>
     */
    public function logout(array $payload): array
    {
        $refreshToken = isset($payload['refresh_token']) ? trim((string)$payload['refresh_token']) : '';
        if ($refreshToken === '') {
            throw new ApiException('AUTH_VALIDATION_ERROR', 'Field refresh_token is required.', 400);
        }

        $pdo = $this->newPdo();
        $table = $this->refreshTokensTable();
        $hash = hash('sha256', $refreshToken);

        try {
            $stmt = $pdo->prepare(
                "UPDATE {$table}
                 SET revoked_at = CURRENT_TIMESTAMP,
                     last_used_at = CURRENT_TIMESTAMP
                 WHERE token_hash = :token_hash
                   AND revoked_at IS NULL"
            );
            $stmt->bindValue(':token_hash', $hash);
            $stmt->execute();

            return [
                'revoked' => $stmt->rowCount() > 0,
            ];
        } catch (PDOException $e) {
            throw new ApiException('DB_ERROR', 'Database query failed.', 500);
        }
    }

    /**
     * @param array<string, mixed> $payload
     * @param array<string, mixed>|null $authContext Caller's verified claims —
     *   drives the tenancy guards (§2.6); null only when tenancy is off.
     * @return array<string, mixed>
     */
    public function createUser(array $payload, ?array $authContext = null): array
    {
        $this->ensureLocalLoginEnabled();

        $email = isset($payload['email']) ? trim((string)$payload['email']) : '';
        $password = isset($payload['password']) ? (string)$payload['password'] : '';
        $displayName = isset($payload['display_name']) ? trim((string)$payload['display_name']) : '';
        $role = isset($payload['role']) ? trim((string)$payload['role']) : 'user';
        $isActive = array_key_exists('is_active', $payload) ? (bool)$payload['is_active'] : true;

        if ($email === '' || $password === '') {
            throw new ApiException('AUTH_VALIDATION_ERROR', 'Fields email and password are required.', 400);
        }
        if (!$this->isValidLogin($email)) {
            throw new ApiException('AUTH_VALIDATION_ERROR', 'Field email must be valid.', 400);
        }

        $minLength = (int)($this->authConfig['password_min_length'] ?? 8);
        if (strlen($password) < $minLength) {
            throw new ApiException('AUTH_VALIDATION_ERROR', "Password must have at least {$minLength} characters.", 400);
        }

        if ($displayName === '') {
            $displayName = strstr($email, '@', true) ?: $email;
        }

        $allowedRoles = $this->assignableRoles();
        if (!in_array($role, $allowedRoles, true)) {
            throw new ApiException('AUTH_VALIDATION_ERROR', 'Role is not allowed.', 400);
        }

        $tenantCol = $this->tenantColumn();
        $tenantId = $tenantCol !== null
            ? $this->resolveCreateUserTenant($payload, $authContext, $role, $tenantCol)
            : null;

        $table = $this->usersTable();
        $hash = password_hash($password, PASSWORD_BCRYPT);
        if (!is_string($hash) || $hash === '') {
            throw new ApiException('AUTH_CONFIG_ERROR', 'Failed to generate password hash.', 500);
        }

        try {
            if ($tenantId !== null) {
                $this->assertTenantActive($tenantId);
            }
            $pdo = $this->newPdo();
            $data = [
                'email' => $email,
                'password_hash' => $hash,
                'display_name' => $displayName,
                'role' => $role,
                'is_active' => $isActive ? 1 : 0,
            ];
            if ($tenantCol !== null) {
                $data[$tenantCol] = $tenantId;
            }
            $newId = $this->insertAuthRow($pdo, $table, $data);

            $result = [
                'id' => $newId,
                'email' => $email,
                'display_name' => $displayName,
                'role' => $role,
                'is_active' => $isActive,
            ];
            if ($tenantCol !== null) {
                $result['tenant_id'] = $tenantId;
            }
            return $result;
        } catch (PDOException $e) {
            // MySQL/PG/SQLite surface a duplicate as SQLSTATE 23000; SQL Server
            // reports SQLSTATE 23000 too but the specific unique/PK violation
            // numbers (2627 / 2601) live in the driver-specific slot.
            $driverCode = is_array($e->errorInfo) && isset($e->errorInfo[1]) ? (int)$e->errorInfo[1] : 0;
            if ((string)$e->getCode() === '23000' || $driverCode === 2627 || $driverCode === 2601) {
                throw new ApiException('AUTH_USER_EXISTS', 'User with this email already exists.', 409);
            }
            throw new ApiException('DB_ERROR', 'Database query failed.', 500);
        }
    }

    /**
     * @param array<string, mixed> $payload
     * @param array<string, mixed>|null $authContext Caller's verified claims —
     *   a scoped admin may only reset passwords inside their own tenant (§2.6).
     * @return array<string, mixed>
     */
    public function changePassword(array $payload, ?array $authContext = null): array
    {
        $this->ensureLocalLoginEnabled();

        $email = isset($payload['email']) ? trim((string)$payload['email']) : '';
        $newPassword = isset($payload['new_password']) ? (string)$payload['new_password'] : '';
        if ($email === '' || $newPassword === '') {
            throw new ApiException('AUTH_VALIDATION_ERROR', 'Fields email and new_password are required.', 400);
        }
        if (!$this->isValidLogin($email)) {
            throw new ApiException('AUTH_VALIDATION_ERROR', 'Field email must be valid.', 400);
        }

        $minLength = (int)($this->authConfig['password_min_length'] ?? 8);
        if (strlen($newPassword) < $minLength) {
            throw new ApiException('AUTH_VALIDATION_ERROR', "Password must have at least {$minLength} characters.", 400);
        }

        $user = $this->findUserByEmail($email, $authContext);
        if ($user === null) {
            throw new ApiException('AUTH_USER_NOT_FOUND', 'User not found.', 404);
        }

        $userId = (int)($user['id'] ?? 0);
        if ($userId <= 0) {
            throw new ApiException('AUTH_CONFIG_ERROR', 'Invalid users table data.', 500);
        }

        $hash = password_hash($newPassword, PASSWORD_BCRYPT);
        if (!is_string($hash) || $hash === '') {
            throw new ApiException('AUTH_CONFIG_ERROR', 'Failed to generate password hash.', 500);
        }

        $usersTable = $this->usersTable();
        $tokensTable = $this->refreshTokensTable();
        $pdo = $this->newPdo();

        try {
            $pdo->beginTransaction();

            $updateUser = $pdo->prepare(
                "UPDATE {$usersTable}
                 SET password_hash = :password_hash, updated_at = CURRENT_TIMESTAMP
                 WHERE id = :id"
            );
            $updateUser->bindValue(':password_hash', $hash);
            $updateUser->bindValue(':id', $userId, PDO::PARAM_INT);
            $updateUser->execute();

            $revoke = $pdo->prepare(
                "UPDATE {$tokensTable}
                 SET revoked_at = CURRENT_TIMESTAMP
                 WHERE user_id = :user_id
                   AND revoked_at IS NULL"
            );
            $revoke->bindValue(':user_id', $userId, PDO::PARAM_INT);
            $revoke->execute();
            $revoked = $revoke->rowCount();

            $pdo->commit();

            return [
                'updated' => true,
                'email' => $email,
                'revoked_refresh_tokens' => $revoked,
            ];
        } catch (PDOException $e) {
            if ($pdo->inTransaction()) {
                $pdo->rollBack();
            }
            throw new ApiException('DB_ERROR', 'Database query failed.', 500);
        }
    }

    /**
     * `auth.change-password`: the signed-in user changes their own password.
     * Requires the current password; revokes all refresh tokens so other
     * sessions must log in again. The user is identified by the verified token
     * (`sub`), never by a payload field.
     *
     * @param array<string, mixed> $payload {current_password, new_password}
     * @param array<string, mixed> $authContext Verified claims from JwtGuard
     * @return array<string, mixed>
     */
    public function changeOwnPassword(array $payload, array $authContext): array
    {
        $this->ensureLocalLoginEnabled();

        $current = isset($payload['current_password']) ? (string)$payload['current_password'] : '';
        $new = isset($payload['new_password']) ? (string)$payload['new_password'] : '';
        if ($current === '' || $new === '') {
            throw new ApiException('AUTH_VALIDATION_ERROR', 'Fields current_password and new_password are required.', 400);
        }
        $minLength = (int)($this->authConfig['password_min_length'] ?? 8);
        if (strlen($new) < $minLength) {
            throw new ApiException('AUTH_VALIDATION_ERROR', "Password must have at least {$minLength} characters.", 400);
        }

        $userId = (int)($authContext['sub'] ?? $authContext['id'] ?? 0);
        if ($userId <= 0) {
            throw new ApiException('AUTH_INVALID', 'Token has no usable user id.', 401);
        }

        $usersTable = $this->usersTable();
        $tokensTable = $this->refreshTokensTable();
        $pdo = $this->newPdo();

        try {
            $select = $pdo->prepare("SELECT password_hash, is_active FROM {$usersTable} WHERE id = :id");
            $select->bindValue(':id', $userId, PDO::PARAM_INT);
            $select->execute();
            $row = $select->fetch(PDO::FETCH_ASSOC);

            $hash = is_array($row) ? (string)($row['password_hash'] ?? '') : '';
            if ($hash === '' || (int)($row['is_active'] ?? 0) !== 1) {
                password_verify($current, self::DUMMY_BCRYPT_HASH);
                throw new ApiException('AUTH_INVALID_CREDENTIALS', 'Current password is incorrect.', 401);
            }
            if (!password_verify($current, $hash)) {
                throw new ApiException('AUTH_INVALID_CREDENTIALS', 'Current password is incorrect.', 401);
            }

            $newHash = password_hash($new, PASSWORD_BCRYPT);
            if (!is_string($newHash) || $newHash === '') {
                throw new ApiException('AUTH_CONFIG_ERROR', 'Failed to generate password hash.', 500);
            }

            $pdo->beginTransaction();
            $update = $pdo->prepare("UPDATE {$usersTable} SET password_hash = :h, updated_at = CURRENT_TIMESTAMP WHERE id = :id");
            $update->bindValue(':h', $newHash);
            $update->bindValue(':id', $userId, PDO::PARAM_INT);
            $update->execute();

            $revoke = $pdo->prepare("UPDATE {$tokensTable} SET revoked_at = CURRENT_TIMESTAMP WHERE user_id = :uid AND revoked_at IS NULL");
            $revoke->bindValue(':uid', $userId, PDO::PARAM_INT);
            $revoke->execute();
            $revoked = $revoke->rowCount();
            $pdo->commit();

            return ['updated' => true, 'revoked_refresh_tokens' => $revoked];
        } catch (PDOException $e) {
            if ($pdo->inTransaction()) {
                $pdo->rollBack();
            }
            throw new ApiException('DB_ERROR', 'Database query failed.', 500);
        }
    }

    /** Login names must look like an email unless `auth.login_format` is 'any' (e.g. "jan.novak"). */
    private function isValidLogin(string $login): bool
    {
        if (($this->authConfig['login_format'] ?? 'email') === 'any') {
            return $login !== '' && strlen($login) <= 190;
        }
        return filter_var($login, FILTER_VALIDATE_EMAIL) !== false;
    }

    /**
     * `auth.exchange` (TOKEN-EXCHANGE.md): swap an app-validated external
     * token (opaque bearer, session id, …) for a Shape-issued JWT, RFC 8693
     * style. Exchange-once by design — the configured source callable
     * verifies the foreign token a single time; afterwards the caller is an
     * ordinary Shape client and tenancy claims, role gates, and suspension
     * checks apply through the standard machinery. Stateless by default
     * (access token only); `issue_refresh_token` opts into the full pair.
     *
     * @param array<string, mixed> $payload
     * @return array<string, mixed>
     */
    public function exchange(array $payload): array
    {
        $cfg = is_array($this->authConfig['exchange'] ?? null) ? $this->authConfig['exchange'] : null;
        $source = $cfg['source'] ?? null;
        if (!is_callable($source)) {
            throw new ApiException('AUTH_NOT_CONFIGURED', 'Token exchange is not configured.', 501, null, [
                'hint' => "Set auth.exchange with a 'source' callable (fn(string \$token, Database \$db): ?array) to enable auth.exchange.",
            ]);
        }

        $token = isset($payload['token']) ? trim((string)$payload['token']) : '';
        if ($token === '') {
            throw new ApiException('AUTH_VALIDATION_ERROR', 'Field token is required.', 400, null, [
                'expected' => ['token' => 'string (the external token to exchange)'],
            ]);
        }

        $db = new Database($this->dbConfig);
        $identity = $source($token, $db);
        if ($identity === null) {
            throw new ApiException('AUTH_EXCHANGE_INVALID', 'External token is invalid, expired, or revoked.', 401, null, [
                'hint' => "Obtain a fresh token from the application's own login flow, then retry the exchange.",
            ]);
        }
        if (!is_array($identity)) {
            throw new ApiException('AUTH_CONFIG_ERROR', 'Token exchange source must return an array or null.', 500);
        }

        $identity = self::applyClaimMapping(
            $identity,
            is_array($cfg['claim_mapping'] ?? null) ? $cfg['claim_mapping'] : []
        );

        $role = isset($identity['role']) ? (string)$identity['role'] : 'user';
        $roleMapping = is_array($cfg['role_mapping'] ?? null) ? $cfg['role_mapping'] : [];
        if (isset($roleMapping[$role]) && is_string($roleMapping[$role]) && $roleMapping[$role] !== '') {
            $role = $roleMapping[$role];
        }
        $identity['role'] = $role;

        // The id may be the app's native type (int, Guid, …) — require
        // presence, not int-ness; buildTokenResponse keeps non-numeric ids
        // verbatim in the `sub` claim.
        if (trim((string)($identity['id'] ?? '')) === '') {
            throw new ApiException('AUTH_CONFIG_ERROR', 'Token exchange source returned no usable user id.', 500, null, [
                'hint' => 'The source callable must return an `id` key (or map one via auth.exchange.claim_mapping).',
            ]);
        }

        // Same §2.3 rules as login: a scoped role needs a live tenant; an
        // unscoped role stays reachable even when its home tenant is suspended.
        if ($this->tenantColumn() !== null && !$this->isUnscopedRole($role)) {
            $this->assertTenantActive($this->normalizeTenantId($identity['tenant_id'] ?? null));
        }

        $response = ($cfg['issue_refresh_token'] ?? false) === true
            ? $this->issueTokenPair($identity)
            : $this->buildTokenResponse($identity, null, 0);

        // Extra identity keys the source chose to return (e.g.
        // organization_name) surface on the response's user object —
        // returning a key IS the pass-through intent. Only the exchange path
        // does this; local-login rows carry secrets and keep the fixed set.
        if (is_array($response['user'] ?? null)) {
            foreach ($identity as $key => $value) {
                if (in_array($key, self::RESERVED_IDENTITY_KEYS, true) || array_key_exists($key, $response['user'])) {
                    continue;
                }
                $response['user'][$key] = $value;
            }
        }
        return $response;
    }

    /**
     * Identity keys consumed by the exchange itself (or too dangerous to
     * echo); everything else passes through to the response's user object.
     */
    private const RESERVED_IDENTITY_KEYS = ['id', 'email', 'role', 'tenant_id', 'display_name', 'password_hash', 'password'];

    /**
     * Rename source-identity keys to Shape's canonical claim names —
     * e.g. OrganizationId → tenant_id. Unmapped keys pass through; a mapping
     * never overwrites an already-present canonical key.
     *
     * @param array<string, mixed> $identity
     * @param array<mixed, mixed> $mapping Raw config — values re-validated here.
     * @return array<string, mixed>
     */
    private static function applyClaimMapping(array $identity, array $mapping): array
    {
        foreach ($mapping as $from => $to) {
            if (!is_string($to) || trim($to) === '' || !array_key_exists($from, $identity)) {
                continue;
            }
            $value = $identity[$from];
            unset($identity[$from]);
            if (!array_key_exists($to, $identity) || $identity[$to] === null) {
                $identity[$to] = $value;
            }
        }
        return $identity;
    }

    /** @param array<string, mixed> $user */
    private function issueTokenPair(array $user): array
    {
        $pdo = $this->newPdo();
        $refreshToken = $this->generateRefreshToken();
        $refreshHash = hash('sha256', $refreshToken);
        $refreshTtl = $this->refreshTokenTtl();
        $table = $this->refreshTokensTable();

        try {
            $this->insertAuthRow($pdo, $table, [
                'user_id' => (int)$user['id'],
                'token_hash' => $refreshHash,
                'expires_at' => gmdate('Y-m-d H:i:s', time() + $refreshTtl),
            ]);
        } catch (PDOException $e) {
            throw new ApiException('DB_ERROR', 'Database query failed.', 500);
        }

        return $this->buildTokenResponse($user, $refreshToken, $refreshTtl);
    }

    /**
     * @param array<string, mixed> $user
     * @param ?string $refreshToken Null on the stateless auth.exchange path —
     *   the client re-exchanges its external token instead of rotating a
     *   Shape refresh token; the refresh keys are then omitted entirely.
     * @return array<string, mixed>
     */
    private function buildTokenResponse(array $user, ?string $refreshToken, int $refreshTtl): array
    {
        $ttl = (int)($this->authConfig['access_token_ttl'] ?? 3600);
        if ($ttl < 60) {
            $ttl = 60;
        }

        // Local users carry int PKs; exchanged identities may carry the app's
        // native id type (ASP.NET Identity Guids, uuids) — keep non-numeric
        // ids verbatim instead of collapsing them to 0 through an int cast.
        $idValue = $user['id'] ?? 0;
        $idInt = is_numeric($idValue) ? (int)$idValue : 0;
        $idText = $idInt > 0 ? (string)$idInt : (string)$idValue;
        $role = (string)($user['role'] ?? 'user');

        $claims = [
            'sub' => $idText,
            'email' => (string)($user['email'] ?? ''),
            'role' => $role,
        ];
        $displayName = (string)($user['display_name'] ?? '');
        if ($displayName !== '') {
            $claims['name'] = $displayName;
        }

        // Tenancy claim (§2.3): server-issued, never a request parameter. The
        // JwtGuard returns the raw payload as the auth context, so tenant_id
        // reaches every consumer (mask scoping, insert stamping) with no
        // further plumbing. Login rows carry the configured column name;
        // exchanged identities the canonical `tenant_id` key. Unscoped roles
        // get an explicit null — they see across tenants (mirrors .NET).
        $tenantCol = $this->tenantColumn();
        if ($tenantCol !== null) {
            $tenantValue = $user[$tenantCol] ?? $user['tenant_id'] ?? null;
            $claims['tenant_id'] = $this->isUnscopedRole($role)
                ? null
                : ($tenantValue !== null ? $this->normalizeTenantId($tenantValue) : null);
        }

        // Dimension scope claims (ROW-SCOPES §2.2): one server-issued claim
        // per configured scope — from users.<column> (SELECTed alongside the
        // user row) or one indexed claim_table SELECT for multi-membership.
        // Roles unscoped for a dimension get an explicit null.
        foreach ($this->scopesConfig() as $column => $spec) {
            $claim = isset($spec['claim']) && is_string($spec['claim']) && $spec['claim'] !== '' ? $spec['claim'] : (string)$column;
            if ($this->isUnscopedForScope($spec, $role)) {
                $claims[$claim] = null;
                continue;
            }
            $claimTable = is_array($spec['claim_table'] ?? null) ? $spec['claim_table'] : null;
            if ($claimTable !== null) {
                $claims[$claim] = $this->fetchScopeMemberships($claimTable, (string)$column, $user['id'] ?? null);
                continue;
            }
            // `self`: the scope value is the caller's own users.id (e.g. a user sees only own rows).
            $value = ($spec['self'] ?? false) === true ? ($user['id'] ?? null) : ($user[$column] ?? $user[$claim] ?? null);
            $value = $value !== null && $value !== '' ? $this->normalizeTenantId($value) : null;
            $claims[$claim] = ($spec['multi'] ?? false) ? ($value === null ? [] : [$value]) : $value;
        }

        $accessToken = $this->issuer->issue($claims, $ttl);

        $response = [
            'access_token' => $accessToken,
            'token_type' => 'Bearer',
            'expires_in' => $ttl,
        ];
        if ($refreshToken !== null) {
            $response['refresh_token'] = $refreshToken;
            $response['refresh_token_expires_in'] = $refreshTtl;
        }
        $userOut = [
            'id' => $idInt > 0 ? $idInt : $idValue,
            'email' => (string)($user['email'] ?? ''),
            'display_name' => $displayName,
            'role' => $role,
        ];
        if ($tenantCol !== null) {
            $userOut['tenant_id'] = $claims['tenant_id'];
        }
        $response['user'] = $userOut;
        return $response;
    }

    /**
     * @param array<string, mixed>|null $authContext When supplied and the
     *   caller is a scoped (non-unscoped-role) admin, the lookup is confined
     *   to the caller's tenant — a tenant admin must not be able to target
     *   another tenant's users by email (§2.6).
     * @return array<string, mixed>|null
     */
    private function findUserByEmail(string $email, ?array $authContext = null): ?array
    {
        $table = $this->usersTable();
        // When tenancy is enabled the user row carries the discriminator; it
        // rides into the issued token as the `tenant_id` claim (§2.3). Scope
        // columns ride along the same way (ROW-SCOPES §2.2).
        $tenantCol = $this->tenantColumn();
        $extra = $tenantCol !== null ? ", {$tenantCol}" : '';
        foreach ($this->scopeUserColumns() as $col) {
            if ($col !== $tenantCol) {
                $extra .= ", {$col}";
            }
        }

        $scopeTenant = null;
        if ($tenantCol !== null && is_array($authContext)
            && !$this->isUnscopedRole(isset($authContext['role']) ? (string)$authContext['role'] : '')
        ) {
            $scopeTenant = array_key_exists('tenant_id', $authContext)
                ? $this->normalizeTenantId($authContext['tenant_id'])
                : null;
            if ($scopeTenant === null) {
                throw new ApiException('AUTH_INVALID', 'Missing tenant_id claim.', 401);
            }
        }
        $tenantWhere = $scopeTenant !== null ? " AND {$tenantCol} = :__scope_tenant" : '';

        try {
            $pdo = $this->newPdo();
            $driver = (string)$pdo->getAttribute(PDO::ATTR_DRIVER_NAME);
            $sql = $driver === 'sqlsrv'
                ? "SELECT TOP 1 id, email, password_hash, display_name, role, is_active{$extra} FROM {$table} WHERE email = :email{$tenantWhere}"
                : "SELECT id, email, password_hash, display_name, role, is_active{$extra} FROM {$table} WHERE email = :email{$tenantWhere} LIMIT 1";
            $stmt = $pdo->prepare($sql);
            $stmt->bindValue(':email', $email);
            if ($scopeTenant !== null) {
                $stmt->bindValue(':__scope_tenant', $scopeTenant, is_int($scopeTenant) ? PDO::PARAM_INT : PDO::PARAM_STR);
            }
            $stmt->execute();
            $row = $stmt->fetch(PDO::FETCH_ASSOC);
            return is_array($row) ? $row : null;
        } catch (PDOException $e) {
            throw new ApiException('DB_ERROR', 'Database query failed.', 500);
        }
    }

    /**
     * Tenancy runtime config injected by ShapeApp when enabled, else null
     * (MULTI-TENANCY-PLAN §2.3).
     *
     * @return array<string, mixed>|null
     */
    private function tenancyConfig(): ?array
    {
        $tenancy = $this->authConfig['tenancy'] ?? null;
        return is_array($tenancy) && $tenancy !== [] ? $tenancy : null;
    }

    /**
     * True when the role sees across tenants (MULTI-TENANCY-PLAN §2.3). Unscoped
     * roles (superadmin) are exempt from the tenant-active gate so the account
     * that can re-activate a suspended tenant is not locked out with it.
     */
    private function isUnscopedRole(string $role): bool
    {
        $tenancy = $this->tenancyConfig();
        if ($tenancy === null || $role === '') {
            return false;
        }
        $unscoped = $tenancy['unscoped_roles'] ?? [];
        return is_array($unscoped) && in_array($role, $unscoped, true);
    }

    /**
     * Preserve a tenant id's native type: numeric ids become int, a non-numeric
     * id (slug/uuid) stays a string. Mirrors BatchExecutor::callerTenantId so
     * both the token claim and the runtime agree — a hard `(int)` cast turned a
     * slug tenant into 0, collapsing every such tenant into one bucket.
     */
    private function normalizeTenantId(mixed $value): int|string|null
    {
        if ($value === null || $value === '') {
            return null;
        }
        return is_numeric($value) ? (int)$value : (string)$value;
    }

    /**
     * Tenancy guards for auth.admin.create-user (§2.6) — mirrors .NET
     * LoginService.CreateUser. Returns the tenant to stamp on the new row.
     *
     * @param array<string, mixed> $payload
     * @param array<string, mixed>|null $authContext
     */
    private function resolveCreateUserTenant(
        array $payload,
        ?array $authContext,
        string $targetRole,
        string $tenantCol
    ): int|string {
        $callerRole = is_array($authContext) && isset($authContext['role']) ? (string)$authContext['role'] : '';
        $callerTenant = is_array($authContext) && array_key_exists('tenant_id', $authContext)
            ? $this->normalizeTenantId($authContext['tenant_id'])
            : null;
        $callerUnscoped = $this->isUnscopedRole($callerRole);
        // The users-table column is the configured discriminator; accept the
        // canonical `tenant_id` claim name too so clients can send the key
        // they read back from the token / manifest.
        $requested = array_key_exists($tenantCol, $payload)
            ? $this->normalizeTenantId($payload[$tenantCol])
            : (array_key_exists('tenant_id', $payload) ? $this->normalizeTenantId($payload['tenant_id']) : null);

        // Privilege-escalation guard: a scoped admin must never mint an
        // unscoped-role account — that would be tenant admin → superadmin in
        // one API call.
        if (!$callerUnscoped && $callerRole !== '' && $this->isUnscopedRole($targetRole)) {
            throw new ApiException('AUTH_FORBIDDEN', 'Scoped admins cannot assign unscoped roles.', 403, null, [
                'hint' => 'Use a scoped role or an unscoped admin account for superadmin provisioning.',
            ]);
        }

        if ($this->isUnscopedRole($targetRole)) {
            // An unscoped-role (superadmin) user still needs a home tenant on
            // its row — users.tenant_id is NOT NULL under strict tenancy. The
            // issued token still carries tenant_id = null for unscoped roles
            // (see buildTokenResponse), so cross-tenant reach is unaffected.
            $tenantId = $requested ?? $callerTenant;
            if ($tenantId === null) {
                throw new ApiException('VALIDATION_ERROR', 'tenant_id required when creating an unscoped-role user.', 400, null, [
                    'detail' => 'TENANT_MISSING',
                    'expected' => 'Supply `tenant_id` for the new account\'s home tenant.',
                ]);
            }
            return $tenantId;
        }

        if ($callerUnscoped) {
            $tenantId = $requested ?? $callerTenant;
            if ($tenantId === null) {
                throw new ApiException('VALIDATION_ERROR', 'tenant_id required for unscoped writes.', 400, null, [
                    'detail' => 'TENANT_MISSING',
                ]);
            }
            return $tenantId;
        }

        if ($callerTenant === null) {
            throw new ApiException('AUTH_INVALID', 'Missing tenant_id claim.', 401);
        }
        // A scoped admin may only create users in its own tenant. A differing
        // client-supplied value is a hard error (§2.6) — silently stamping the
        // caller's tenant would create the user somewhere the caller did not
        // intend, with no signal.
        if ($requested !== null && $requested !== $callerTenant) {
            throw new ApiException('VALIDATION_ERROR', "tenant_id does not match the caller's tenant.", 400, null, [
                'detail' => 'TENANT_MISMATCH',
                'expected' => $callerTenant,
            ]);
        }
        return $callerTenant;
    }

    /**
     * Normalized `scopes` config injected by ShapeApp (ROW-SCOPES §2.2);
     * [] when row scopes are off. Keyed by discriminator column.
     *
     * @return array<string, array<string, mixed>>
     */
    private function scopesConfig(): array
    {
        $scopes = $this->authConfig['scopes'] ?? null;
        return is_array($scopes) ? array_filter($scopes, 'is_array') : [];
    }

    /**
     * Scope columns read straight off the users row (claim_table scopes read
     * a membership table instead). Operator config interpolated into SQL —
     * held to the same identifier rule as tenantColumn().
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
        $table = $this->safeTableName((string)($claimTable['table'] ?? ''), '');
        $userColumn = (string)($claimTable['user_column'] ?? 'user_id');
        if (!preg_match('/^[A-Za-z_][A-Za-z0-9_]*$/', $userColumn) || !preg_match('/^[A-Za-z_][A-Za-z0-9_]*$/', $column)) {
            throw new ApiException('AUTH_CONFIG_ERROR', 'Invalid scope claim_table configuration.', 500);
        }
        $id = is_numeric($userId) ? (int)$userId : (string)$userId;

        try {
            $pdo = $this->newPdo();
            $stmt = $pdo->prepare("SELECT {$column} FROM {$table} WHERE {$userColumn} = :uid");
            $stmt->bindValue(':uid', $id, is_int($id) ? PDO::PARAM_INT : PDO::PARAM_STR);
            $stmt->execute();
            $values = [];
            while (($v = $stmt->fetchColumn()) !== false) {
                if ($v !== null && $v !== '') {
                    $values[] = $this->normalizeTenantId($v);
                }
            }
            return $values;
        } catch (PDOException $e) {
            throw new ApiException('DB_ERROR', 'Database query failed.', 500);
        }
    }

    /** The tenancy discriminator column name, or null when tenancy is off. */
    private function tenantColumn(): ?string
    {
        $tenancy = $this->tenancyConfig();
        if ($tenancy === null) {
            return null;
        }
        $col = isset($tenancy['column']) && is_string($tenancy['column']) && $tenancy['column'] !== ''
            ? $tenancy['column']
            : 'tenant_id';
        // Column name is operator config, never client input — but it is
        // interpolated into SQL, so hold it to the same identifier rule as
        // the table names.
        if (!preg_match('/^[A-Za-z_][A-Za-z0-9_]*$/', $col)) {
            throw new ApiException('AUTH_CONFIG_ERROR', 'Invalid tenancy column name.', 500);
        }
        return $col;
    }

    /**
     * Reject a login / refresh against a suspended or unknown tenant
     * (MULTI-TENANCY-PLAN §2.3 / §2.7). Suspension is `is_active = 0` or a
     * non-null soft-delete column on the registry row; both are recognized by
     * convention (SELECT * keeps this schema-agnostic — the registry is a
     * small internal table).
     */
    private function assertTenantActive(int|string|null $tenantId): void
    {
        $tenancy = $this->tenancyConfig();
        if ($tenancy === null) {
            return;
        }
        if ($tenantId === null || $tenantId === '' || (is_int($tenantId) && $tenantId <= 0)) {
            throw new ApiException('TENANT_SUSPENDED', 'Your account is not assigned to an active tenant.', 403);
        }

        $registry = $this->safeTableName(
            isset($tenancy['table']) && is_string($tenancy['table']) ? $tenancy['table'] : 'tenants',
            'tenants'
        );

        try {
            $pdo = $this->newPdo();
            $driver = (string)$pdo->getAttribute(PDO::ATTR_DRIVER_NAME);
            $sql = $driver === 'sqlsrv'
                ? "SELECT TOP 1 * FROM {$registry} WHERE id = :id"
                : "SELECT * FROM {$registry} WHERE id = :id LIMIT 1";
            $stmt = $pdo->prepare($sql);
            $stmt->bindValue(':id', $tenantId, is_int($tenantId) ? PDO::PARAM_INT : PDO::PARAM_STR);
            $stmt->execute();
            $row = $stmt->fetch(PDO::FETCH_ASSOC);
        } catch (PDOException $e) {
            throw new ApiException('DB_ERROR', 'Database query failed.', 500);
        }

        if (!is_array($row)) {
            throw new ApiException('TENANT_SUSPENDED', 'Your tenant no longer exists.', 403);
        }
        if (array_key_exists('is_active', $row) && (int)$row['is_active'] !== 1) {
            throw new ApiException('TENANT_SUSPENDED', 'Your tenant has been suspended.', 403);
        }
        if (array_key_exists('deleted_at', $row) && $row['deleted_at'] !== null) {
            throw new ApiException('TENANT_SUSPENDED', 'Your tenant has been removed.', 403);
        }
    }

    /**
     * Refuse password-bearing operations when the deployment doesn't have a
     * `password_hash` column to read or write — typical OIDC-only setups
     * (Entra ID / Auth0 / Okta with a `users` schema that only carries
     * `oid` / `email` / `role`). Without this short-circuit the same calls
     * would hit the DB and surface as a generic `DB_ERROR` ("column does
     * not exist"), giving the operator no diagnostic clue.
     *
     * Set by ShapeApp::buildDatabaseConfig at boot when the cached bundle's
     * `users_lacks_password_hash` flag is true; the dev can override by
     * setting `auth.local_login_disabled => false` explicitly.
     */
    private function ensureLocalLoginEnabled(): void
    {
        if (($this->authConfig['local_login_disabled'] ?? false) !== true) {
            return;
        }
        throw new ApiException(
            'AUTH_NOT_CONFIGURED',
            'Local password login is not configured for this deployment.',
            501,
            null,
            [
                'expected' => 'a `users.password_hash` column on the configured users_table',
                'hint'     => 'This backend appears to be OIDC-only. Authenticate via the configured identity provider and pass the resulting access token as Authorization: Bearer <token>. To re-enable local-JWT login, add a `password_hash VARCHAR(255) NOT NULL` column to the users table and clear the Shape cache.',
                'ref'      => 'docs/shape/AUTH.md',
            ]
        );
    }

    private function newPdo(): PDO
    {
        $dsn = (string)($this->dbConfig['dsn'] ?? '');
        $user = (string)($this->dbConfig['user'] ?? '');
        $pass = (string)($this->dbConfig['pass'] ?? '');
        $options = is_array($this->dbConfig['options'] ?? null) ? $this->dbConfig['options'] : [];

        $options[PDO::ATTR_ERRMODE] = PDO::ERRMODE_EXCEPTION;
        $options[PDO::ATTR_DEFAULT_FETCH_MODE] = PDO::FETCH_ASSOC;

        try {
            return new PDO($dsn, $user, $pass, $options);
        } catch (PDOException $e) {
            throw new ApiException('DB_ERROR', 'Database connection failed.', 500);
        }
    }

    /**
     * INSERT into an auth table, honoring `pk_assignment`: legacy schemas
     * where users / refresh_tokens carry a plain int PK (no identity /
     * autoincrement / default) need the framework's MAX+1 allocation. The
     * auth tables are secret — never introspected — so only an explicit
     * 'manual' in the map (injected into auth config by ShapeApp) activates
     * the manual path here; the build-time 'auto' schema detection cannot
     * run against a raw PDO. One shared code path with BatchExecutor via
     * Database::insertWithManualPkOnPdo.
     *
     * Note for manual-PK users tables: a duplicate email surfaces from the
     * allocation retry loop as DB_ERROR (unique violations there are
     * indistinguishable from PK races), not AUTH_USER_EXISTS.
     *
     * @param array<string, mixed> $data Column => value; columns are
     *   hardcoded by the callers, never caller-supplied input.
     */
    private function insertAuthRow(PDO $pdo, string $table, array $data): int
    {
        $mode = Database::resolvePkAssignmentMode($this->authConfig['pk_assignment'] ?? null, $table);
        if ($mode === 'manual') {
            $driver = (string)$pdo->getAttribute(PDO::ATTR_DRIVER_NAME);
            return Database::insertWithManualPkOnPdo($pdo, $driver, $table, 'id', $data);
        }

        $columns = array_keys($data);
        $sql = "INSERT INTO {$table} (" . implode(', ', $columns) . ') VALUES ('
            . implode(', ', array_map(static fn(string $c): string => ':' . $c, $columns)) . ')';
        return $this->insertReturningId($pdo, $sql, $data);
    }

    /**
     * Driver-portable INSERT-then-return-id on a raw PDO (kept here because
     * LoginService manages its own transaction on the raw PDO). PG appends
     * `RETURNING id`; SQL Server injects `OUTPUT INSERTED.id` before VALUES;
     * MySQL/SQLite use lastInsertId.
     *
     * @param array<string, mixed> $params Bound as :name placeholders
     */
    private function insertReturningId(PDO $pdo, string $sql, array $params): int
    {
        $driver = (string)$pdo->getAttribute(PDO::ATTR_DRIVER_NAME);
        if ($driver === 'pgsql') {
            $sql .= ' RETURNING id';
        } elseif ($driver === 'sqlsrv') {
            $sql = preg_replace('/\s+VALUES\s*\(/i', ' OUTPUT INSERTED.id VALUES (', $sql, 1) ?? $sql;
        }
        $stmt = $pdo->prepare($sql);
        foreach ($params as $name => $value) {
            $stmt->bindValue(':' . $name, $value);
        }
        $stmt->execute();
        if ($driver === 'pgsql' || $driver === 'sqlsrv') {
            $row = $stmt->fetch(PDO::FETCH_NUM);
            return is_array($row) ? (int)($row[0] ?? 0) : 0;
        }
        return (int)$pdo->lastInsertId();
    }

    private function usersTable(): string
    {
        return $this->safeTableName((string)($this->authConfig['users_table'] ?? 'users'), 'users');
    }

    private function refreshTokensTable(): string
    {
        return $this->safeTableName((string)($this->authConfig['refresh_tokens_table'] ?? 'refresh_tokens'), 'refresh_tokens');
    }

    private function refreshTokenTtl(): int
    {
        $ttl = (int)($this->authConfig['refresh_token_ttl'] ?? 2592000);
        return $ttl < 300 ? 300 : $ttl;
    }

    private function safeTableName(string $table, string $fallback): string
    {
        if (!preg_match('/^[A-Za-z_][A-Za-z0-9_]*$/', $table)) {
            throw new ApiException('AUTH_CONFIG_ERROR', 'Invalid table name.', 500);
        }
        return $table !== '' ? $table : $fallback;
    }

    private function generateRefreshToken(): string
    {
        return rtrim(strtr(base64_encode(random_bytes(48)), '+/', '-_'), '=');
    }

    /** @return list<string> */
    private function assignableRoles(): array
    {
        $roles = $this->authConfig['assignable_roles'] ?? ['user', 'admin'];
        if (!is_array($roles)) {
            return ['user', 'admin'];
        }

        $out = [];
        foreach ($roles as $role) {
            if (is_string($role) && $role !== '') {
                $out[] = $role;
            }
        }
        return $out === [] ? ['user', 'admin'] : $out;
    }
}
