<?php

declare(strict_types=1);

namespace ShapeRuntime\Http;

use ShapeRuntime\Auth\JwtGuard;
use ShapeRuntime\Core\ApiException;

final class AdminAuth
{
    /** @param array<string, mixed> $authConfig */
    public function __construct(private readonly array $authConfig)
    {
    }

    /** @return array<string, mixed> */
    public function authorizeAdmin(): array
    {
        if (($this->authConfig['driver'] ?? 'none') !== 'jwt') {
            throw new ApiException('AUTH_CONFIG_ERROR', 'Admin endpoints require auth driver jwt.', 500);
        }

        $guard = new JwtGuard($this->authConfig);
        $claims = $guard->authorize($this->normalizeHeaders($this->readHeaders()));

        $role = isset($claims['role']) ? (string)$claims['role'] : '';
        if ($role !== 'admin' && !$this->isUnscopedRole($role)) {
            throw new ApiException('AUTH_FORBIDDEN', 'Admin role is required.', 403);
        }

        return $claims;
    }

    /**
     * Unscoped roles (superadmin) may reach the admin endpoints too — they
     * administer every tenant, so gating on the literal 'admin' role would
     * lock them out (MULTI-TENANCY-PLAN §2.6). Mirrors .NET AdminAuth.
     */
    private function isUnscopedRole(string $role): bool
    {
        $tenancy = $this->authConfig['tenancy'] ?? null;
        if ($role === '' || !is_array($tenancy)) {
            return false;
        }
        $unscoped = $tenancy['unscoped_roles'] ?? [];
        return is_array($unscoped) && in_array($role, $unscoped, true);
    }

    /** @return array<string, string> */
    private function normalizeHeaders(array $headers): array
    {
        $out = [];
        foreach ($headers as $k => $v) {
            $out[strtolower((string)$k)] = (string)$v;
        }
        return $out;
    }

    /** @return array<string, string> */
    private function readHeaders(): array
    {
        if (function_exists('getallheaders')) {
            $headers = getallheaders();
            return is_array($headers) ? $headers : [];
        }

        $headers = [];
        foreach ($_SERVER as $k => $v) {
            if (strncmp($k, 'HTTP_', 5) !== 0) {
                continue;
            }
            $name = strtolower(str_replace('_', '-', substr($k, 5)));
            $headers[$name] = (string)$v;
        }
        return $headers;
    }
}
