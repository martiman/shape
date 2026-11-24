<?php

declare(strict_types=1);

namespace Shape\Mcp;

use RuntimeException;

/**
 * Protocol-level error to be reported as a JSON-RPC error response.
 * For tool-call failures (where the client expects an isError content
 * object inside a successful response), throw the original ApiException
 * instead — McpServer wraps it with the self-documenting envelope.
 */
final class McpException extends RuntimeException
{
    public function __construct(
        private readonly int $mcpCode,
        string $message,
    ) {
        parent::__construct($message);
    }

    public function mcpCode(): int
    {
        return $this->mcpCode;
    }
}
