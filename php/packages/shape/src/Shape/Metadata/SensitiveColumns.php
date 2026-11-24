<?php

declare(strict_types=1);

namespace Shape\Metadata;

/**
 * Central policy for columns that must not be exposed or written through the
 * generic auto-CRUD surface. Mirrors the .NET Shape\Metadata\SensitiveColumns.
 *
 *  - isReadSensitive():  secret material (password / token / secret / salt
 *    families). Stripped from admin read masks.
 *  - isWriteSensitive(): the read-sensitive set PLUS privilege / account-state
 *    columns (role, is_active, is_admin, …). Excluded from a writable mask's
 *    `writable_columns` allowlist so a client cannot escalate its own role or
 *    overwrite a password hash through the generic `mutation` op
 *    (SECURITY-REVIEW H1).
 *
 * Name-heuristic by necessity (DB-first mode has no per-field access
 * metadata); it is a safe default, not the last line of defense — the
 * allowlist itself rejects any column not explicitly listed.
 */
final class SensitiveColumns
{
    private const READ_EXACT = ['token', 'secret', 'password', 'salt'];
    private const READ_PREFIXES = ['password_', 'secret_', 'salt_', 'mfa_', 'totp_'];
    private const READ_SUFFIXES = ['_hash', '_secret', '_token'];

    /** Privilege / account-state columns: readable, never client-writable. */
    private const WRITE_EXACT = [
        'role', 'roles', 'is_active', 'active', 'is_admin', 'is_superuser',
        'is_staff', 'is_superadmin', 'is_root', 'superuser', 'admin',
        'permissions', 'perms', 'scopes', 'email_verified', 'verified',
    ];

    public static function isReadSensitive(string $name): bool
    {
        $lower = strtolower($name);
        if (in_array($lower, self::READ_EXACT, true)) {
            return true;
        }
        foreach (self::READ_PREFIXES as $prefix) {
            if (str_starts_with($lower, $prefix)) {
                return true;
            }
        }
        foreach (self::READ_SUFFIXES as $suffix) {
            if (str_ends_with($lower, $suffix)) {
                return true;
            }
        }
        return false;
    }

    public static function isWriteSensitive(string $name): bool
    {
        if (self::isReadSensitive($name)) {
            return true;
        }
        return in_array(strtolower($name), self::WRITE_EXACT, true);
    }
}
