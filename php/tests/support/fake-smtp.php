<?php

/**
 * One-shot fake SMTP server for SmtpMailer tests: listens on a random local port, prints the port,
 * serves a single connection and writes everything the client sent to the file given as argv[1].
 */

declare(strict_types=1);

$out = $argv[1];
$srv = stream_socket_server('tcp://127.0.0.1:0', $errno, $errstr);
$name = stream_socket_get_name($srv, false);
fwrite(STDOUT, substr((string)strrchr((string)$name, ':'), 1) . "\n");
fflush(STDOUT);

$conn = stream_socket_accept($srv, 10);
$log = '';
$say = static function (string $s) use ($conn): void {
    fwrite($conn, $s . "\r\n");
};
$say('220 fake ESMTP');
$data = false;
while (($line = fgets($conn)) !== false) {
    $log .= $line;
    if ($data) {
        if ($line === ".\r\n") {
            $data = false;
            $say('250 queued');
        }
        continue;
    }
    $cmd = strtoupper(substr($line, 0, 4));
    if ($cmd === 'EHLO') {
        $say('250-fake');
        $say('250 AUTH PLAIN');
    } elseif ($cmd === 'AUTH') {
        $say('235 ok');
    } elseif ($cmd === 'MAIL' || $cmd === 'RCPT') {
        $say('250 ok');
    } elseif ($cmd === 'DATA') {
        $data = true;
        $say('354 go');
    } elseif ($cmd === 'QUIT') {
        $say('221 bye');
        break;
    } else {
        $say('500 what');
    }
}
file_put_contents($out, $log);
