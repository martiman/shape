<?php

/**
 * E-mail notifications: rules (mask + recipients), per-user visibility, deduplication, outbox delivery
 * with retries, header-injection hygiene, and the SMTP/MIME transport.
 *
 *   vendor/bin/tester tests/Notifications.phpt
 */

declare(strict_types=1);

use Shape\Notify\CollectingMailer;
use Shape\Notify\MimeMessage;
use Shape\Notify\Notifier;
use Shape\Notify\SmtpMailer;
use ShapeRuntime\Auth\JwtIssuer;
use ShapeRuntime\Auth\LoginService;
use ShapeRuntime\Core\BatchExecutor;
use ShapeRuntime\Core\Database;
use ShapeRuntime\Core\MaskRegistry;
use ShapeRuntime\Core\ValidatorRegistry;
use Tester\Assert;

require __DIR__ . '/../vendor/autoload.php';

Tester\Environment::setup();

$tmp = sys_get_temp_dir() . '/shape-notify-' . bin2hex(random_bytes(4)) . '.sqlite';
$dbCfg = ['dsn' => 'sqlite:' . $tmp];
$db = new Database($dbCfg);
$db->exec('CREATE TABLE users (id INTEGER PRIMARY KEY AUTOINCREMENT, email TEXT NOT NULL, password_hash TEXT NOT NULL, display_name TEXT, role TEXT NOT NULL, is_active INT NOT NULL DEFAULT 1, notify_email INT NOT NULL DEFAULT 1)', []);
$db->exec('CREATE TABLE contracts (id INTEGER PRIMARY KEY AUTOINCREMENT, owner_id INT NOT NULL, title TEXT NOT NULL, end_date TEXT NOT NULL)', []);
$db->exec('CREATE TABLE notification_outbox (id INTEGER PRIMARY KEY AUTOINCREMENT, rule TEXT NOT NULL, recipient TEXT NOT NULL, dedupe_key TEXT NOT NULL, subject TEXT NOT NULL, body_text TEXT NOT NULL, body_html TEXT NULL, status TEXT NOT NULL DEFAULT \'pending\', attempts INT NOT NULL DEFAULT 0, last_error TEXT NULL, created_at TEXT NOT NULL, sent_at TEXT NULL)', []);
foreach ([['alice@x.cz', 'user', 1, 1], ['bob@x.cz', 'user', 1, 1], ['root@x.cz', 'admin', 1, 1], ['carol@x.cz', 'user', 0, 1], ["evil@x.cz\nBcc: spy@x.cz", 'user', 1, 1], ['dave@x.cz', 'user', 1, 0]] as [$e, $r, $active, $notify]) {
    $db->exec('INSERT INTO users (email, password_hash, display_name, role, is_active, notify_email) VALUES (:e, :p, :n, :r, :a, :o)', ['e' => $e, 'p' => 'x', 'n' => $e, 'r' => $r, 'a' => $active, 'o' => $notify]);
}
// ids: alice 1, bob 2, root 3, carol 4 (inactive), evil 5, dave 6 (opted out)
$db->exec("INSERT INTO contracts (owner_id, title, end_date) VALUES (1, 'A-lease', '2026-12-01'), (1, 'A-<b>support</b>', '2026-12-10'), (2, 'B-license', '2026-12-05'), (4, 'C-old', '2026-12-02'), (6, 'D-one', '2026-12-03')", []);

$scopes = ['owner_id' => ['self' => true, 'unscoped_roles' => ['admin']]];
$base = 'SELECT c.id, c.title, c.end_date, u.email AS owner_email FROM contracts c JOIN users u ON u.id = c.owner_id WHERE c.end_date <= :until';
$masks = [
    'contracts.expiring' => [
        'sql' => $base . ' AND c.owner_id = :__scope_owner_id ORDER BY c.end_date',
        'sql_unscoped' => $base . ' ORDER BY c.end_date',
        'params' => ['until' => ['type' => 'string', 'required' => true]],
        'roles' => ['admin', 'user'],
        'row_scopes' => ['owner_id'],
        'description' => 'Contracts ending on or before a date.',
    ],
];
$auth = ['driver' => 'jwt', 'jwt_secret' => 'test-secret-0123456789abcdef0123456789', 'jwt_issuer' => 't', 'jwt_audience' => 't', 'users_table' => 'users', 'scopes' => $scopes];
$executor = new BatchExecutor($db, new MaskRegistry($masks), new ValidatorRegistry([]));
$executor->setScoping([], $scopes);
$login = new LoginService($dbCfg, $auth, new JwtIssuer($auth));

$now = 1_800_000_000;
$clock = static function () use (&$now): int {
    return $now;
};
$make = static function (array $rules, ?CollectingMailer $mailer = null) use ($db, $executor, $login, &$clock): array {
    $mailer ??= new CollectingMailer();
    return [new Notifier($db, $executor, $login, $mailer, ['rules' => $rules, 'max_attempts' => 3], ['table' => 'users'], $clock), $mailer];
};
$outbox = static fn(): array => array_map(
    static fn(array $r): array => array_combine(['id', 'rule', 'recipient', 'status', 'attempts', 'subject', 'text', 'html'], $r),
    $db->run('SELECT id, rule, recipient, status, attempts, subject, body_text, body_html FROM notification_outbox ORDER BY id', [])['rows']
);

$userRule = [
    'mask' => 'contracts.expiring', 'params' => ['until' => '2026-12-31'],
    'to' => ['users' => ['roles' => ['user'], 'optout_column' => 'notify_email']],
    'subject' => "Contracts: {count}\r\nBcc: spy@x.cz", 'intro' => 'Ending soon:', 'columns' => ['title', 'end_date'], 'labels' => ['title' => 'Contract'],
];

// ── to: users — each person gets only what THEY may see; inactive / opted-out / bad addresses skipped ─
[$n, $mailer] = $make(['expiring' => $userRule]);
$s = $n->enqueue();
Assert::same(2, $s['queued']);                                   // alice, bob
Assert::same([], $s['rules']['expiring']['errors']);
$ob = $outbox();
Assert::same(['alice@x.cz', 'bob@x.cz'], array_column($ob, 'recipient'));
Assert::contains('A-lease', $ob[0]['text']);
Assert::notContains('B-license', $ob[0]['text']);                // bob's contract never reaches alice
Assert::contains('B-license', $ob[1]['text']);
Assert::notContains('A-lease', $ob[1]['text']);
Assert::notContains('C-old', $ob[0]['text'] . $ob[1]['text']);  // inactive owner's row is nobody's
Assert::same('Contracts: 2 Bcc: spy@x.cz', $ob[0]['subject']);   // CR/LF removed: cannot inject a header
Assert::contains('&lt;b&gt;support&lt;/b&gt;', $ob[0]['html']);  // HTML escaped
Assert::notContains('<b>support</b>', $ob[0]['html']);
Assert::contains('Contract', $ob[0]['html']);                    // label applied

// ── dedupe: same list not queued again; a changed list is; after repeat_days the same list is ─────
Assert::same(0, $n->enqueue()['queued']);
$db->exec("INSERT INTO contracts (owner_id, title, end_date) VALUES (2, 'B-new', '2026-12-20')", []);
Assert::same(1, $n->enqueue()['queued']);                        // only bob's list changed
$now += 8 * 86400;
Assert::same(2, $n->enqueue()['queued']);                        // 7 days later both lists repeat

// ── dry run writes nothing ───────────────────────────────────────────────────────────────────────
$now += 8 * 86400;
$before = count($outbox());
Assert::same(2, $n->enqueue(null, true)['queued']);               // reports what WOULD be queued ...
Assert::same($before, count($outbox()));                          // ... and writes nothing

// ── admin (unscoped role) sees every row ─────────────────────────────────────────────────────────
[$n2] = $make(['all' => ['mask' => 'contracts.expiring', 'params' => ['until' => '2026-12-31'], 'to' => ['users' => ['roles' => ['admin']]], 'columns' => ['title']]]);
$n2->enqueue();
$all = array_values(array_filter($outbox(), static fn(array $r): bool => $r['rule'] === 'all'));
Assert::count(1, $all);
Assert::same('root@x.cz', $all[0]['recipient']);
foreach (['A-lease', 'B-license', 'C-old', 'D-one'] as $t) {
    Assert::contains($t, $all[0]['text']);
}

// ── to: column / emails run as the explicit run_as context ───────────────────────────────────────
[$n3] = $make([
    'by_owner' => ['mask' => 'contracts.expiring', 'params' => ['until' => '2026-12-31'], 'to' => ['column' => 'owner_email', 'run_as' => ['role' => 'admin']], 'columns' => ['title']],
    'legal' => ['mask' => 'contracts.expiring', 'params' => ['until' => '2026-12-04'], 'to' => ['emails' => ['legal@x.cz', "bad\naddr"], 'run_as' => ['role' => 'admin']], 'columns' => ['title']],
    'no_run_as' => ['mask' => 'contracts.expiring', 'params' => ['until' => '2026-12-31'], 'to' => ['emails' => ['x@x.cz']]],
    'bad_mask' => ['mask' => 'nope.nothing', 'to' => ['users' => []]],
]);
$s = $n3->enqueue();
$recips = static fn(string $rule): array => array_column(array_filter($outbox(), static fn(array $r): bool => $r['rule'] === $rule), 'recipient');
$byOwner = array_values($recips('by_owner'));
sort($byOwner);
Assert::same(['alice@x.cz', 'bob@x.cz', 'carol@x.cz', 'dave@x.cz'], $byOwner);
Assert::same(['legal@x.cz'], array_values($recips('legal')));     // injected address skipped
Assert::count(1, $s['rules']['no_run_as']['errors']);            // misconfigured rule reported ...
Assert::count(1, $s['rules']['bad_mask']['errors']);
Assert::same(1, $s['rules']['legal']['queued']);                 // ... and does not stop the others

// ── delivery: sent / retry / permanently failed, one bad recipient does not block the rest ──────────
$db->exec('DELETE FROM notification_outbox', []);
$mailer = new CollectingMailer(['bob@x.cz' => true]);
[$n4] = $make(['expiring' => $userRule], $mailer);
$n4->enqueue();
$d = $n4->deliver();
Assert::same(['sent' => 1, 'failed' => 0, 'retry' => 1], $d);
Assert::same(['alice@x.cz'], array_column($mailer->sent, 'to'));
$d = $n4->deliver();
Assert::same(['sent' => 0, 'failed' => 0, 'retry' => 1], $d);
$d = $n4->deliver();                                              // third attempt: permanently failed
Assert::same(['sent' => 0, 'failed' => 1, 'retry' => 0], $d);
$rows = $outbox();
Assert::same(['sent', 'failed'], array_column($rows, 'status'));
Assert::same(3, (int)$rows[1]['attempts']);
Assert::same(['sent' => 0, 'failed' => 0, 'retry' => 0], $n4->deliver());   // nothing left to do

// ── MIME: header injection refused, non-ASCII encoded, multipart when HTML is present ────────────────
Assert::exception(static fn() => MimeMessage::assertAddress("a@b.cz\r\nBcc: x@y.cz"), RuntimeException::class);
Assert::exception(static fn() => MimeMessage::assertAddress('Name <a@b.cz>'), RuntimeException::class);
$m = MimeMessage::build('from@x.cz', 'Šéf', 'to@x.cz', "Příliš žluťoučký\nBcc: z@z.cz", 'text', '<p>html</p>');
Assert::match('#^=\?UTF-8\?B\?#', $m['headers']['Subject']);
Assert::notContains("\n", $m['headers']['Subject']);
Assert::match('#^multipart/alternative#', $m['headers']['Content-Type']);

// ── SMTP transport against a local fake server ───────────────────────────────────────────────────
$captured = sys_get_temp_dir() . '/shape-smtp-' . bin2hex(random_bytes(4)) . '.txt';
$proc = proc_open([PHP_BINARY, __DIR__ . '/support/fake-smtp.php', $captured], [1 => ['pipe', 'w'], 2 => ['pipe', 'w']], $pipes);
Assert::true(is_resource($proc));
$port = (int)trim((string)fgets($pipes[1]));
Assert::true($port > 0);
(new SmtpMailer('127.0.0.1', $port, 'none', 'user', 'secret', 'noreply@x.cz', 'Shape'))
    ->send('alice@x.cz', 'Smlouvy: žluťoučký kůň', "Dobrý den\n.tečka na začátku řádku", '<p>Dobrý den</p>');
proc_close($proc);
$log = (string)file_get_contents($captured);
Assert::contains('RCPT TO:<alice@x.cz>', $log);
Assert::contains('AUTH PLAIN ' . base64_encode("\0user\0secret"), $log);
Assert::match('#Subject: =\?UTF-8\?B\?#', $log);
Assert::contains('multipart/alternative', $log);
@unlink($captured);

// ── event rules: evaluated right after a committed mutation, mailed immediately ─────────────────────
$evMasks = $masks + ['contracts' => ['sql' => 'SELECT id, owner_id, title, end_date FROM contracts', 'params' => [], 'roles' => ['admin', 'user'], 'writable' => true, 'description' => 'x']];
$evExec = new BatchExecutor($db, new MaskRegistry($evMasks), new ValidatorRegistry([]));
$evExec->setScoping([], $scopes);
$evMailer = new CollectingMailer();
$evCfg = ['max_attempts' => 3, 'rules' => [
    'new_contract' => [
        'on' => ['table' => 'contracts', 'actions' => ['insert']],
        'mask' => 'contracts.expiring', 'params' => ['until' => '2027-12-31'],
        'to' => ['users' => ['roles' => ['user'], 'optout_column' => 'notify_email']],
        'subject' => 'Contracts: {count}', 'repeat_days' => 0,
    ],
    'later_rule' => [
        'on' => ['table' => ['contracts'], 'actions' => ['delete']], 'send' => 'later',
        'mask' => 'contracts.expiring', 'params' => ['until' => '2027-12-31'],
        'to' => ['emails' => ['legal@x.cz'], 'run_as' => ['role' => 'admin']],
    ],
]];
Assert::true(Notifier::hasEventRules($evCfg));
Assert::false(Notifier::hasEventRules(['rules' => ['a' => ['mask' => 'x']]]));
$evNotifier = new Notifier($db, $evExec, $login, $evMailer, $evCfg, ['table' => 'users'], $clock);
$evExec->setChangeListener(static fn(array $c) => $evNotifier->onChanges($c));
$admin = ['id' => 3, 'role' => 'admin', 'owner_id' => null];
$ob0 = count($outbox());

// an update does not match `actions: [insert]` -> nothing queued
$evExec->executeMutations([['ref' => 'u', 'table' => 'contracts', 'action' => 'update', 'filter' => ['id' => 1], 'data' => ['title' => 'A-lease2']]], $admin, true);
Assert::same($ob0, count($outbox()));
Assert::count(0, $evMailer->sent);

// an insert queues AND sends right away, to each user who may see rows (alice, bob), none to opted-out / inactive users
$evExec->executeMutations([['ref' => 'i', 'table' => 'contracts', 'action' => 'insert', 'data' => ['owner_id' => 1, 'title' => 'E-event', 'end_date' => '2026-12-30']]], $admin, true);
Assert::same(['alice@x.cz', 'bob@x.cz'], array_column($evMailer->sent, 'to'));
$evRows = array_values(array_filter($outbox(), static fn(array $r): bool => $r['rule'] === 'new_contract'));
Assert::same(['sent', 'sent'], array_column($evRows, 'status'));
Assert::contains('E-event', $evRows[count($evRows) - 2]['text']);

// repeat_days = 0: the same list is mailed again for the next event
$evExec->executeMutations([['ref' => 'i2', 'table' => 'contracts', 'action' => 'insert', 'data' => ['owner_id' => 1, 'title' => 'F-event', 'end_date' => '2026-12-31']]], $admin, false);
Assert::count(4, $evMailer->sent);

// 'send' => 'later' queues but leaves the mail pending for cron
$evExec->executeMutations([['ref' => 'd', 'table' => 'contracts', 'action' => 'delete', 'filter' => ['title' => 'F-event']]], $admin, true);
$later = array_values(array_filter($outbox(), static fn(array $r): bool => $r['rule'] === 'later_rule'));
Assert::same(['pending'], array_column($later, 'status'));
Assert::count(4, $evMailer->sent);

// a failing rule / transport never fails the mutation
$boom = new Notifier($db, $evExec, $login, new class implements \Shape\Notify\MailerInterface {
    public function send(string $to, string $subject, string $text, ?string $html = null): void
    {
        throw new \RuntimeException('smtp down');
    }
}, $evCfg, ['table' => 'users'], $clock);
$evExec->setChangeListener(static fn(array $c) => $boom->onChanges($c));
$r = $evExec->executeMutations([['ref' => 'i3', 'table' => 'contracts', 'action' => 'insert', 'data' => ['owner_id' => 2, 'title' => 'G-event', 'end_date' => '2026-12-31']]], $admin, true);
Assert::same(1, $r['results'][0]['affected']);
Assert::true(in_array('pending', array_column($outbox(), 'status'), true));   // failed mails wait for a retry

@unlink($tmp);
echo "OK\n";
