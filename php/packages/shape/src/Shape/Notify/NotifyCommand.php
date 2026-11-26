<?php

declare(strict_types=1);

namespace Shape\Notify;

use Shape\ShapeRouter;
use ShapeRuntime\Auth\JwtIssuer;
use ShapeRuntime\Auth\LoginService;
use ShapeRuntime\Core\Database;
use ShapeRuntime\Core\MaskRegistry;
use ShapeRuntime\Core\ValidatorRegistry;

/**
 * CLI entry point for e-mail notifications — run it from cron (shared hostings have it):
 *
 *   # bin/notify.php in your app
 *   $config = Shape\ShapeApp::fromDatabase(__DIR__ . '/../config/database.php');
 *   exit(Shape\Notify\NotifyCommand::run($config, $argv));
 *
 *   # crontab: every morning at 7:00
 *   0 7 * * *  php /path/to/app/bin/notify.php
 *
 * Options: --rule=NAME (one rule), --dry-run (show what would be queued/sent, write nothing),
 * --enqueue-only, --send-only. Exit code 0 = all fine, 1 = a rule failed or a mail permanently failed.
 */
final class NotifyCommand
{
    /**
     * @param array<string, mixed> $config result of ShapeApp::fromDatabase / fromEntities
     * @param list<string> $argv
     */
    public static function run(array $config, array $argv, ?MailerInterface $mailer = null, ?callable $out = null): int
    {
        $out ??= static function (string $line): void {
            fwrite(STDOUT, $line . "\n");
        };
        $opts = self::parseArgs($argv);

        $notify = is_array($config['notifications'] ?? null) ? $config['notifications'] : null;
        if ($notify === null) {
            $out("No 'notifications' block in the config.");
            return 1;
        }

        try {
            $db = new Database($config['db'] ?? []);
            $auth = is_array($config['auth'] ?? null) ? $config['auth'] : [];
            $executor = ShapeRouter::buildExecutor(
                $config,
                is_array($config['_shape'] ?? null) ? $config['_shape'] : [],
                $db,
                new MaskRegistry($config['masks'] ?? []),
                new ValidatorRegistry($config['validators'] ?? [])
            );
            $login = new LoginService($config['db'] ?? [], $auth, new JwtIssuer($auth));
            $mailer ??= self::mailerFromConfig(is_array($notify['transport'] ?? null) ? $notify['transport'] : ['driver' => 'log']);
            $notifier = new Notifier($db, $executor, $login, $mailer, $notify, ['table' => $auth['users_table'] ?? 'users']);

            $failed = false;
            if (!$opts['send-only']) {
                $s = $notifier->enqueue($opts['rule'], $opts['dry-run']);
                foreach ($s['rules'] as $name => $r) {
                    $out(sprintf('%s: %d queued, %d skipped (already sent)', $name, $r['queued'], $r['skipped']));
                    foreach ($r['errors'] as $e) {
                        $out("  ERROR {$name}: {$e}");
                        $failed = true;
                    }
                }
            }
            if (!$opts['enqueue-only']) {
                $d = $notifier->deliver($opts['dry-run']);
                $out(sprintf('delivery: %d %s, %d to retry, %d failed', $d['sent'], $opts['dry-run'] ? 'would be sent' : 'sent', $d['retry'], $d['failed']));
                $failed = $failed || $d['failed'] > 0;
            }
            return $failed ? 1 : 0;
        } catch (\Throwable $e) {
            $out('notify failed: ' . $e->getMessage());
            return 1;
        }
    }

    /**
     * @param array<string, mixed> $t `notifications.transport`
     */
    public static function mailerFromConfig(array $t): MailerInterface
    {
        $driver = (string)($t['driver'] ?? 'log');
        $from = (string)($t['from'] ?? '');
        $fromName = (string)($t['from_name'] ?? '');
        return match ($driver) {
            'log' => new LogMailer(),
            'mail' => new MailFunctionMailer($from, $fromName),
            'smtp' => new SmtpMailer(
                (string)($t['host'] ?? ''),
                (int)($t['port'] ?? 587),
                (string)($t['encryption'] ?? 'tls'),
                isset($t['user']) ? (string)$t['user'] : null,
                isset($t['pass']) ? (string)$t['pass'] : null,
                $from,
                $fromName,
                (int)($t['timeout'] ?? 15)
            ),
            default => throw new \InvalidArgumentException("Unknown notifications transport driver '{$driver}' (log, mail, smtp)."),
        };
    }

    /**
     * @param list<string> $argv
     * @return array{rule:?string, dry-run:bool, enqueue-only:bool, send-only:bool}
     */
    private static function parseArgs(array $argv): array
    {
        $o = ['rule' => null, 'dry-run' => false, 'enqueue-only' => false, 'send-only' => false];
        foreach (array_slice($argv, 1) as $a) {
            if (str_starts_with($a, '--rule=')) {
                $o['rule'] = substr($a, 7);
            } elseif ($a === '--dry-run') {
                $o['dry-run'] = true;
            } elseif ($a === '--enqueue-only') {
                $o['enqueue-only'] = true;
            } elseif ($a === '--send-only') {
                $o['send-only'] = true;
            }
        }
        return $o;
    }
}
