<?php

declare(strict_types=1);

namespace Shape\Mcp;

/**
 * Wraps McpServer in a JSON-RPC over stdio loop. Each line on stdin is one
 * JSON-RPC message; each non-null response is one line on stdout.
 *
 * Used by `bin/shape-mcp` for Claude Desktop / Cursor / Cline local
 * integrations. The client spawns this process once per session and keeps
 * the pipe open for the conversation's lifetime.
 *
 * Does not rely on any specific message framing beyond newline-delimited
 * JSON, which all MCP clients with stdio support produce.
 */
final class StdioTransport
{
    public function __construct(private readonly McpServer $server)
    {
    }

    public function serve(): void
    {
        // Disable output buffering so each response flushes immediately.
        while (ob_get_level() > 0) {
            ob_end_flush();
        }

        $stdin = fopen('php://stdin', 'rb');
        $stdout = fopen('php://stdout', 'wb');
        if ($stdin === false || $stdout === false) {
            return;
        }

        while (!feof($stdin)) {
            $line = fgets($stdin);
            if ($line === false) {
                break;
            }
            $line = trim($line);
            if ($line === '') {
                continue;
            }

            $message = json_decode($line, true);
            if (!is_array($message)) {
                fwrite($stdout, $this->encode([
                    'jsonrpc' => '2.0',
                    'id' => null,
                    'error' => ['code' => -32700, 'message' => 'Parse error'],
                ]));
                continue;
            }

            $response = $this->server->handle($message);
            if ($response !== null) {
                fwrite($stdout, $this->encode($response));
            }
        }

        fclose($stdin);
        fclose($stdout);
    }

    /** @param array<string, mixed> $response */
    private function encode(array $response): string
    {
        return json_encode($response, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES) . "\n";
    }
}
