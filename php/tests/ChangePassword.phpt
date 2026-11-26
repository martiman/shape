<?php

/**
 * Self-service password change (`auth.change-password`) and `auth.login_format => any`
 * for admin change-password with non-email login names. SQLite file DB.
 *
 *   vendor/bin/tester tests/ChangePassword.phpt
 */

declare(strict_types=1);

use ShapeRuntime\Auth\JwtGuard;
use ShapeRuntime\Auth\JwtIssuer;
use ShapeRuntime\Auth\LoginService;
use ShapeRuntime\Core\ApiException;
use ShapeRuntime\Core\Database;
use Tester\Assert;

require __DIR__ . '/../vendor/autoload.php';

Tester\Environment::setup();

$catch = static function (callable $fn): string {
    try {
        $fn();
    } catch (ApiException $e) {
        return $e->apiCode();
    }
    return '(none)';
};

$pw = password_hash('oldpass123', PASSWORD_BCRYPT);
$tmp = tempnam(sys_get_temp_dir(), 'shape_pw_') . '.sqlite';
$db = new Database(['dsn' => 'sqlite:' . $tmp]);
$db->exec('CREATE TABLE users (id INTEGER PRIMARY KEY AUTOINCREMENT, email VARCHAR(100) NOT NULL, password_hash VARCHAR(255) NOT NULL, display_name VARCHAR(100), role VARCHAR(20) NOT NULL, is_active INT NOT NULL DEFAULT 1, updated_at VARCHAR(32))', []);
$db->exec('CREATE TABLE refresh_tokens (id INTEGER PRIMARY KEY AUTOINCREMENT, user_id INT NOT NULL, token_hash VARCHAR(64) NOT NULL, expires_at VARCHAR(32) NOT NULL, revoked_at VARCHAR(32), replaced_by_token_id INT, last_used_at VARCHAR(32))', []);
$db->exec("INSERT INTO users (email, password_hash, display_name, role) VALUES ('jan.novak', '{$pw}', 'Jan', 'user'), ('eva', '{$pw}', 'Eva', 'user')", []);

$auth = ['driver' => 'jwt', 'jwt_secret' => 'test-secret-please-ignore-0123456789', 'assignable_roles' => ['user', 'admin'], 'login_format' => 'any'];
$login = new LoginService(['dsn' => 'sqlite:' . $tmp], $auth, new JwtIssuer($auth));
$guard = new JwtGuard($auth);
$claims = static fn(array $tok): array => $guard->authorize(['authorization' => 'Bearer ' . $tok['access_token']]);

$tok = $login->login(['email' => 'jan.novak', 'password' => 'oldpass123']);
$ctx = $claims($tok);

// Validation + wrong current password.
Assert::same('AUTH_VALIDATION_ERROR', $catch(fn() => $login->changeOwnPassword(['new_password' => 'newpass456'], $ctx)));
Assert::same('AUTH_VALIDATION_ERROR', $catch(fn() => $login->changeOwnPassword(['current_password' => 'oldpass123', 'new_password' => 'short'], $ctx)));
Assert::same('AUTH_INVALID_CREDENTIALS', $catch(fn() => $login->changeOwnPassword(['current_password' => 'nope', 'new_password' => 'newpass456'], $ctx)));
Assert::same('AUTH_INVALID', $catch(fn() => $login->changeOwnPassword(['current_password' => 'oldpass123', 'new_password' => 'newpass456'], ['role' => 'user'])));

// Success: old password stops working, new one works, refresh tokens are revoked.
$res = $login->changeOwnPassword(['current_password' => 'oldpass123', 'new_password' => 'newpass456'], $ctx);
Assert::true($res['updated']);
Assert::same(1, $res['revoked_refresh_tokens']);
Assert::same('AUTH_INVALID_CREDENTIALS', $catch(fn() => $login->login(['email' => 'jan.novak', 'password' => 'oldpass123'])));
Assert::notSame('', $login->login(['email' => 'jan.novak', 'password' => 'newpass456'])['access_token']);
Assert::notSame('(none)', $catch(fn() => $login->refresh(['refresh_token' => $tok['refresh_token']])));

// Only the caller's own password changed.
Assert::notSame('', $login->login(['email' => 'eva', 'password' => 'oldpass123'])['access_token']);

// Admin reset works for a non-email login when login_format = any; email format still rejected by default.
Assert::true($login->changePassword(['email' => 'eva', 'new_password' => 'reset-pass-1'])['updated']);
Assert::notSame('', $login->login(['email' => 'eva', 'password' => 'reset-pass-1'])['access_token']);
$strict = new LoginService(['dsn' => 'sqlite:' . $tmp], ['login_format' => 'email'] + $auth, new JwtIssuer($auth));
Assert::same('AUTH_VALIDATION_ERROR', $catch(fn() => $strict->changePassword(['email' => 'eva', 'new_password' => 'reset-pass-2'])));

@unlink($tmp);
echo "OK\n";
