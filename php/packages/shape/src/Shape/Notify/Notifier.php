<?php

declare(strict_types=1);

namespace Shape\Notify;

use ShapeRuntime\Auth\LoginService;
use ShapeRuntime\Core\ApiException;
use ShapeRuntime\Core\BatchExecutor;
use ShapeRuntime\Core\Database;

/**
 * E-mail notifications: a rule is "run this mask, mail the rows". Two phases so a mail server hiccup
 * never loses or duplicates a notification:
 *
 *   enqueue()  evaluate the rules and write one `notification_outbox` row per recipient (deduplicated)
 *   deliver()  send the pending rows through the transport, retrying up to `max_attempts`
 *
 * Who sees what is decided by the same rules as the API. `to: users` runs the mask AS each user, with the
 * claims their own token would carry (row scopes, tenancy, mask roles), so a notification can never contain a
 * row that user could not open. `to: column` / `to: emails` run the mask once as the configured `run_as`
 * context — an explicit, reviewable choice.
 *
 * Rule config (see DB-FIRST.md, "E-mail notifications"):
 *
 *   'expiring_contracts' => [
 *       'mask'        => 'contracts.expiring',
 *       'params'      => ['days' => 60],
 *       'to'          => ['users' => ['roles' => ['user']]],     // or ['column' => 'owner_email', 'run_as' => ['role' => 'admin']]
 *                                                               // or ['emails' => ['legal@example.com'], 'run_as' => ['role' => 'admin']]
 *       'key'         => 'id',          // column that identifies an item (default: id)
 *       'repeat_days' => 7,             // same list to the same person at most once per N days (default 7)
 *       'max_rows'    => 50,            // rows listed per mail (default 50, max 199); "… and N more" beyond
 *       'subject'     => 'Contracts ending soon: {count}',
 *       'intro'       => 'These contracts end within 60 days:',
 *       'columns'     => ['title', 'end_date'],
 *       'labels'      => ['title' => 'Contract', 'end_date' => 'Ends'],
 *   ]
 *
 * Event rules: add `'on' => ['table' => 'tickets', 'actions' => ['insert', 'update']]` (`actions` optional, `table`
 * may be a list) and the rule is also evaluated right after a successful mutation of that table, see
 * {@see onChanges()}. `'send' => 'later'` leaves delivery to the next cron run (default: send immediately).
 */
final class Notifier
{
    public const DEFAULT_OUTBOX = 'notification_outbox';
    private const DEFAULT_MAX_ROWS = 50;
    private const MAX_ROWS_CAP = 199;
    private const DEFAULT_REPEAT_DAYS = 7;

    /**
     * @param array<string, mixed> $config the `notifications` block
     * @param array<string, mixed> $usersConfig ['table' => 'users'] (login table of the app)
     */
    public function __construct(
        private readonly Database $db,
        private readonly BatchExecutor $executor,
        private readonly LoginService $login,
        private readonly MailerInterface $mailer,
        private readonly array $config,
        private readonly array $usersConfig = [],
        private readonly ?\Closure $clock = null,
    ) {
    }

    /**
     * Evaluate rules and queue mails.
     *
     * @return array{queued:int, skipped:int, rules: array<string, array{queued:int, skipped:int, errors:list<string>}>}
     */
    public function enqueue(?string $onlyRule = null, bool $dryRun = false): array
    {
        $summary = ['queued' => 0, 'skipped' => 0, 'rules' => []];
        $rules = is_array($this->config['rules'] ?? null) ? $this->config['rules'] : [];
        if ($onlyRule !== null && !isset($rules[$onlyRule])) {
            throw new \InvalidArgumentException("Unknown notification rule '{$onlyRule}'. Known: " . implode(', ', array_keys($rules)) . '.');
        }

        foreach ($rules as $name => $rule) {
            $name = (string)$name;
            if ($onlyRule !== null && $name !== $onlyRule) {
                continue;
            }
            $r = ['queued' => 0, 'skipped' => 0, 'errors' => []];
            try {
                foreach ($this->batches($name, is_array($rule) ? $rule : []) as $batch) {
                    $mail = $this->render($name, $rule, $batch['rows'], $batch['total'], $batch['more']);
                    $key = $this->dedupeKey($name, $batch['to'], $rule, $batch['rows']);
                    if ($this->recentlySent($name, $batch['to'], $key, (int)($rule['repeat_days'] ?? self::DEFAULT_REPEAT_DAYS))) {
                        $r['skipped']++;
                        continue;
                    }
                    if (!$dryRun) {
                        $this->insertOutbox($name, $batch['to'], $key, $mail);
                    }
                    $r['queued']++;
                }
            } catch (\Throwable $e) {
                // One broken rule (bad mask, unknown param) must not stop the others.
                $r['errors'][] = $e instanceof ApiException ? $e->apiCode() . ': ' . $e->getMessage() : $e->getMessage();
            }
            $summary['rules'][$name] = $r;
            $summary['queued'] += $r['queued'];
            $summary['skipped'] += $r['skipped'];
        }
        return $summary;
    }

    /**
     * Event trigger: evaluate the rules whose `on` matches a committed change, then (unless a rule says
     * `'send' => 'later'`) deliver the queue right away. Never throws: the mutation that caused it has already been
     * committed, and a failed mail simply stays pending for the next cron run.
     *
     * @param list<array{table:string,action:string,affected:int}> $changes
     * @return array{queued:int, sent:int}
     */
    public function onChanges(array $changes): array
    {
        $out = ['queued' => 0, 'sent' => 0];
        $rules = is_array($this->config['rules'] ?? null) ? $this->config['rules'] : [];
        $sendNow = false;
        foreach ($rules as $name => $rule) {
            if (!is_array($rule) || !self::matches($rule, $changes)) {
                continue;
            }
            $s = $this->enqueue((string)$name);
            $out['queued'] += $s['queued'];
            $sendNow = $sendNow || ($s['queued'] > 0 && ($rule['send'] ?? 'now') !== 'later');
        }
        if ($sendNow) {
            try {
                $out['sent'] = $this->deliver()['sent'];
            } catch (\Throwable) {
                // stays pending
            }
        }
        return $out;
    }

    /**
     * @param array<string, mixed> $rule
     * @param list<array{table:string,action:string,affected:int}> $changes
     */
    private static function matches(array $rule, array $changes): bool
    {
        $on = $rule['on'] ?? null;
        if (!is_array($on)) {
            return false;
        }
        $tables = array_values(array_map('strval', (array)($on['table'] ?? [])));
        $actions = array_values(array_map('strval', (array)($on['actions'] ?? [])));
        foreach ($changes as $c) {
            if (($tables === [] || in_array($c['table'], $tables, true)) && ($actions === [] || in_array($c['action'], $actions, true))) {
                return true;
            }
        }
        return false;
    }

    /** Whether any rule reacts to events (so the router knows to wire a listener). */
    public static function hasEventRules(array $config): bool
    {
        foreach (is_array($config['rules'] ?? null) ? $config['rules'] : [] as $rule) {
            if (is_array($rule) && is_array($rule['on'] ?? null)) {
                return true;
            }
        }
        return false;
    }

    /**
     * Send pending outbox rows.
     *
     * @return array{sent:int, failed:int, retry:int}
     */
    public function deliver(bool $dryRun = false, int $limit = 100): array
    {
        $out = ['sent' => 0, 'failed' => 0, 'retry' => 0];
        $maxAttempts = max(1, (int)($this->config['max_attempts'] ?? 3));
        $t = $this->db->quoteIdentifier($this->outbox());
        $res = $this->db->run(
            "SELECT id, recipient, subject, body_text, body_html, attempts FROM {$t} WHERE status = 'pending' AND attempts < :max ORDER BY id",
            ['max' => $maxAttempts]
        );
        $n = 0;
        foreach ($res['rows'] as $row) {
            if (++$n > $limit) {
                break;
            }
            $r = array_combine($res['columns'], $row);
            if ($dryRun) {
                $out['sent']++;
                continue;
            }
            try {
                $this->mailer->send((string)$r['recipient'], (string)$r['subject'], (string)$r['body_text'], $r['body_html'] !== null ? (string)$r['body_html'] : null);
                $this->db->exec(
                    "UPDATE {$t} SET status = 'sent', attempts = attempts + 1, sent_at = :now, last_error = NULL WHERE id = :id",
                    ['now' => $this->now(), 'id' => $r['id']]
                );
                $out['sent']++;
            } catch (\Throwable $e) {
                $attempts = (int)$r['attempts'] + 1;
                $final = $attempts >= $maxAttempts;
                $this->db->exec(
                    "UPDATE {$t} SET status = :st, attempts = :a, last_error = :err WHERE id = :id",
                    ['st' => $final ? 'failed' : 'pending', 'a' => $attempts, 'err' => mb_substr($e->getMessage(), 0, 500), 'id' => $r['id']]
                );
                $out[$final ? 'failed' : 'retry']++;
            }
        }
        return $out;
    }

    // ── recipients ────────────────────────────────────────────────────

    /**
     * @param array<string, mixed> $rule
     * @return \Generator<int, array{to:string, rows:list<array<string, mixed>>, total:int, more:int}>
     */
    private function batches(string $name, array $rule): \Generator
    {
        $mask = (string)($rule['mask'] ?? '');
        if ($mask === '') {
            throw new \InvalidArgumentException("Rule '{$name}' needs a 'mask'.");
        }
        $to = is_array($rule['to'] ?? null) ? $rule['to'] : [];
        $params = is_array($rule['params'] ?? null) ? $rule['params'] : [];
        $maxRows = max(1, min(self::MAX_ROWS_CAP, (int)($rule['max_rows'] ?? self::DEFAULT_MAX_ROWS)));

        if (isset($to['users'])) {
            $cfg = is_array($to['users']) ? $to['users'] : [];
            foreach ($this->userEmails($cfg) as $email) {
                $ctx = $this->login->authContextForEmail($email);
                if ($ctx === null) {
                    continue;
                }
                [$rows, $more] = $this->runMask($mask, $params, $ctx, $maxRows);
                if ($rows !== []) {
                    yield ['to' => $email, 'rows' => $rows, 'total' => count($rows) + $more, 'more' => $more];
                }
            }
            return;
        }

        $runAs = is_array($to['run_as'] ?? null) ? $to['run_as'] : null;
        if ($runAs === null || (string)($runAs['role'] ?? '') === '') {
            throw new \InvalidArgumentException("Rule '{$name}': 'to.column' / 'to.emails' need 'run_as' => ['role' => ...] (the context the mask runs in).");
        }
        // The context is explicit config; the system id keeps created_by / audit stamps recognisable.
        $ctx = $runAs + ['id' => 0, 'sub' => 'notifications', 'email' => 'notifications@localhost'];
        [$rows, $more] = $this->runMask($mask, $params, $ctx, self::MAX_ROWS_CAP);

        if (isset($to['column'])) {
            $col = (string)$to['column'];
            $groups = [];
            foreach ($rows as $row) {
                $addr = isset($row[$col]) && is_string($row[$col]) ? strtolower(trim($row[$col])) : '';
                if ($addr !== '' && filter_var($addr, FILTER_VALIDATE_EMAIL) !== false) {
                    $groups[$addr][] = $row;
                }
            }
            foreach ($groups as $addr => $g) {
                $shown = array_slice($g, 0, $maxRows);
                yield ['to' => (string)$addr, 'rows' => $shown, 'total' => count($g), 'more' => count($g) - count($shown)];
            }
            return;
        }

        if (isset($to['emails']) && is_array($to['emails'])) {
            if ($rows === []) {
                return;
            }
            $shown = array_slice($rows, 0, $maxRows);
            foreach ($to['emails'] as $addr) {
                if (is_string($addr) && filter_var(trim($addr), FILTER_VALIDATE_EMAIL) !== false) {
                    yield ['to' => trim($addr), 'rows' => $shown, 'total' => count($rows) + $more, 'more' => count($rows) + $more - count($shown)];
                }
            }
            return;
        }

        throw new \InvalidArgumentException("Rule '{$name}': 'to' must be one of users / column / emails.");
    }

    /**
     * @param array<string, mixed> $cfg ['roles' => [...], 'optout_column' => 'notify_email']
     * @return list<string>
     */
    private function userEmails(array $cfg): array
    {
        $table = $this->db->quoteIdentifier((string)($this->usersConfig['table'] ?? 'users'));
        $where = ['is_active = 1'];
        $params = [];
        $roles = is_array($cfg['roles'] ?? null) ? array_values(array_map('strval', $cfg['roles'])) : [];
        if ($roles !== []) {
            $where[] = 'role IN (:roles)';
            $params['roles'] = $roles;
        }
        $opt = isset($cfg['optout_column']) ? (string)$cfg['optout_column'] : '';
        if ($opt !== '') {
            if (preg_match('/^[A-Za-z_][A-Za-z0-9_]*$/', $opt) !== 1) {
                throw new \InvalidArgumentException("Invalid optout_column '{$opt}'.");
            }
            $where[] = $this->db->quoteIdentifier($opt) . ' <> 0';   // a user who switched it off is skipped
        }
        $res = $this->db->run('SELECT email FROM ' . $table . ' WHERE ' . implode(' AND ', $where) . ' ORDER BY id', $params);
        $emails = [];
        foreach ($res['rows'] as $row) {
            $e = trim((string)$row[0]);
            if ($e !== '' && filter_var($e, FILTER_VALIDATE_EMAIL) !== false) {
                $emails[] = $e;
            }
        }
        return $emails;
    }

    /**
     * @param array<string, mixed> $params
     * @param array<string, mixed> $ctx
     * @return array{0:list<array<string, mixed>>, 1:int} rows (at most $top) and how many more matched
     */
    private function runMask(string $mask, array $params, array $ctx, int $top): array
    {
        $r = $this->executor->execute(
            [['ref' => 'notify', 'sql' => $mask, 'params' => $params, 'options' => ['top' => $top + 1]]],
            $ctx
        )['results'][0] ?? [];
        $cols = is_array($r['meta']['columns'] ?? null) ? $r['meta']['columns'] : [];
        $rows = [];
        foreach (is_array($r['rows'] ?? null) ? $r['rows'] : [] as $row) {
            $rows[] = array_combine($cols, $row);
        }
        $more = max(0, count($rows) - $top);
        return [array_slice($rows, 0, $top), $more];
    }

    // ── rendering, dedupe, outbox ────────────────────────────────────

    /**
     * @param array<string, mixed> $rule
     * @param list<array<string, mixed>> $rows
     * @return array{subject:string, text:string, html:string}
     */
    private function render(string $name, array $rule, array $rows, int $total, int $more): array
    {
        $vars = ['count' => (string)$total, 'rule' => $name];
        $subject = MimeMessage::cleanHeader(self::interpolate((string)($rule['subject'] ?? '{rule}: {count}'), $vars));
        $intro = self::interpolate((string)($rule['intro'] ?? ''), $vars);
        $outro = self::interpolate((string)($rule['outro'] ?? ''), $vars);

        $columns = is_array($rule['columns'] ?? null) && $rule['columns'] !== []
            ? array_values(array_map('strval', $rule['columns']))
            : array_keys($rows[0] ?? []);
        $labels = is_array($rule['labels'] ?? null) ? $rule['labels'] : [];
        $label = static fn(string $c): string => isset($labels[$c]) ? (string)$labels[$c] : $c;

        $text = ($intro !== '' ? $intro . "\n\n" : '');
        $html = '<p>' . ($intro !== '' ? nl2br(self::h($intro)) : '') . '</p><table border="1" cellpadding="6" cellspacing="0" style="border-collapse:collapse"><tr>';
        foreach ($columns as $c) {
            $html .= '<th align="left">' . self::h($label($c)) . '</th>';
        }
        $html .= '</tr>';
        foreach ($rows as $row) {
            $cells = [];
            $html .= '<tr>';
            foreach ($columns as $c) {
                $v = self::cell($row[$c] ?? null);
                $cells[] = $v;
                $html .= '<td>' . self::h($v) . '</td>';
            }
            $html .= '</tr>';
            $text .= '- ' . implode(' | ', $cells) . "\n";
        }
        $html .= '</table>';
        if ($more > 0) {
            $text .= "… +{$more}\n";
            $html .= '<p>… +' . $more . '</p>';
        }
        if ($outro !== '') {
            $text .= "\n" . $outro . "\n";
            $html .= '<p>' . nl2br(self::h($outro)) . '</p>';
        }
        return ['subject' => $subject, 'text' => $text, 'html' => $html];
    }

    /** @param array<string, string> $vars */
    private static function interpolate(string $tpl, array $vars): string
    {
        return strtr($tpl, array_combine(array_map(static fn(string $k): string => '{' . $k . '}', array_keys($vars)), array_values($vars)));
    }

    private static function cell(mixed $v): string
    {
        if ($v === null) {
            return '';
        }
        return is_scalar($v) ? (string)$v : (string)json_encode($v, JSON_UNESCAPED_UNICODE);
    }

    private static function h(string $s): string
    {
        return htmlspecialchars($s, ENT_QUOTES | ENT_SUBSTITUTE, 'UTF-8');
    }

    /**
     * Stable fingerprint of "this list to this person": the same items again within `repeat_days` are not
     * re-sent, a changed list (new or removed item) is.
     *
     * @param array<string, mixed> $ruleCfg
     * @param list<array<string, mixed>> $rows
     */
    private function dedupeKey(string $rule, string $to, array $ruleCfg, array $rows): string
    {
        $key = (string)($ruleCfg['key'] ?? 'id');
        $ids = [];
        foreach ($rows as $row) {
            $ids[] = array_key_exists($key, $row) && is_scalar($row[$key]) ? (string)$row[$key] : hash('sha256', (string)json_encode($row));
        }
        sort($ids);
        return hash('sha256', $rule . '|' . strtolower($to) . '|' . implode(',', $ids));
    }

    private function recentlySent(string $rule, string $to, string $key, int $repeatDays): bool
    {
        if ($repeatDays <= 0) {
            return false;   // repeat_days = 0: no de-duplication (every event mails)
        }
        $t = $this->db->quoteIdentifier($this->outbox());
        $since = gmdate('Y-m-d H:i:s', $this->time() - max(0, $repeatDays) * 86400);
        $r = $this->db->run(
            "SELECT COUNT(*) FROM {$t} WHERE rule = :r AND recipient = :to AND dedupe_key = :k AND created_at >= :since",
            ['r' => $rule, 'to' => $to, 'k' => $key, 'since' => $since]
        );
        return (int)($r['rows'][0][0] ?? 0) > 0;
    }

    /** @param array{subject:string, text:string, html:string} $mail */
    private function insertOutbox(string $rule, string $to, string $key, array $mail): void
    {
        $t = $this->db->quoteIdentifier($this->outbox());
        $this->db->exec(
            "INSERT INTO {$t} (rule, recipient, dedupe_key, subject, body_text, body_html, status, attempts, created_at) VALUES (:r, :to, :k, :s, :t, :h, 'pending', 0, :now)",
            ['r' => $rule, 'to' => $to, 'k' => $key, 's' => mb_substr($mail['subject'], 0, 255), 't' => $mail['text'], 'h' => $mail['html'], 'now' => $this->now()]
        );
    }

    private function outbox(): string
    {
        $t = (string)($this->config['outbox_table'] ?? self::DEFAULT_OUTBOX);
        if (preg_match('/^[A-Za-z_][A-Za-z0-9_]*$/', $t) !== 1) {
            throw new \InvalidArgumentException("Invalid outbox_table '{$t}'.");
        }
        return $t;
    }

    private function time(): int
    {
        return $this->clock !== null ? (int)($this->clock)() : time();
    }

    private function now(): string
    {
        return gmdate('Y-m-d H:i:s', $this->time());
    }
}
