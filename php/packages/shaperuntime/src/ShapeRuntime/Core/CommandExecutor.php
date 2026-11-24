<?php

declare(strict_types=1);

namespace ShapeRuntime\Core;

use Throwable;

/**
 * Executes domain-level business logic commands.
 * Commands are higher-level operations that may involve multiple mutations,
 * validations, and business rule enforcement.
 */
final class CommandExecutor
{
    /**
     * @param array<string, callable> $handlers
     * @param array<string, list<string>> $commandRoles Per-command role
     *   allowlist (SECURITY-REVIEW M9). A command with no entry is unrestricted
     *   (mirrors the mask-role default); an entry gates invocation by role.
     */
    public function __construct(
        private readonly Database $database,
        private readonly BatchExecutor $executor,
        private readonly array $handlers = [],
        private readonly array $commandRoles = []
    ) {
    }

    /**
     * Execute a named command with parameters.
     * @param array<string, mixed> $params
     * @param array<string, mixed> $authContext
     * @return array<string, mixed>
     */
    public function execute(string $commandName, array $params, array $authContext = []): array
    {
        // SECURITY-REVIEW M9: role-gate command invocation before the handler
        // lookup so an unauthorized caller cannot probe which commands exist.
        $allowedRoles = $this->commandRoles[$commandName] ?? null;
        if (is_array($allowedRoles) && $allowedRoles !== []) {
            $callerRole = isset($authContext['role']) ? (string)$authContext['role'] : '';
            if ($callerRole === '' || !in_array($callerRole, $allowedRoles, true)) {
                throw new ApiException(
                    'AUTH_FORBIDDEN',
                    "Your role cannot run command '{$commandName}'.",
                    403,
                    null,
                    [
                        'expected' => ['role' => 'one of: ' . implode(', ', $allowedRoles)],
                        'hint' => $callerRole === ''
                            ? "Authenticate first via POST {op:'auth.token'} and pass the access token as Authorization: Bearer <token>."
                            : "Command '{$commandName}' is restricted to: " . implode(', ', $allowedRoles) . '.',
                    ]
                );
            }
        }

        if (!isset($this->handlers[$commandName])) {
            $available = array_keys($this->handlers);
            throw new ApiException(
                'COMMAND_NOT_FOUND',
                "Command '{$commandName}' does not exist.",
                404,
                null,
                [
                    'expected' => ['payload.name' => $available !== []
                        ? 'one of: ' . implode(', ', $available)
                        : 'string (no commands configured)'],
                    'example' => $available !== []
                        ? ['name' => $available[0], 'params' => new \stdClass()]
                        : null,
                    'hint' => $available !== []
                        ? 'Available commands: ' . implode(', ', $available) . '.'
                        : 'No commands are configured for this app.',
                ]
            );
        }

        $handler = $this->handlers[$commandName];
        if (!is_callable($handler)) {
            throw new ApiException('COMMAND_ERROR', "Command '{$commandName}' handler is not callable.", 500);
        }

        try {
            $context = new CommandContext($this->database, $this->executor, $authContext);
            $result = $handler($params, $context);

            if (!is_array($result)) {
                throw new ApiException('COMMAND_ERROR', "Command must return an array.", 500);
            }

            return $result;
        } catch (ApiException $e) {
            throw $e;
        } catch (Throwable $e) {
            // SECURITY-REVIEW M7: don't echo the raw exception message to the
            // client (leaks internals). Log it server-side under a correlation id.
            $correlationId = bin2hex(random_bytes(8));
            error_log("[shape][COMMAND_ERROR][{$correlationId}] {$commandName}: " . $e->getMessage());
            throw new ApiException(
                'COMMAND_ERROR',
                'Command execution failed.',
                500,
                null,
                ['correlation_id' => $correlationId]
            );
        }
    }
}

/**
 * Context passed to command handlers providing access to database and mutations.
 */
final class CommandContext
{
    public function __construct(
        private readonly Database $database,
        private readonly BatchExecutor $executor,
        private readonly array $authContext
    ) {
    }

    /**
     * Execute mutations within the command context.
     * @param list<array<string, mixed>> $mutations
     * @param bool $useTransaction
     * @return array<string, mixed>
     */
    public function executeMutations(array $mutations, bool $useTransaction = true): array
    {
        return $this->executor->executeMutations($mutations, $this->authContext, $useTransaction);
    }

    /**
     * Begin a transaction.
     */
    public function beginTransaction(): void
    {
        $this->database->beginTransaction();
    }

    /**
     * Commit current transaction.
     */
    public function commit(): void
    {
        $this->database->commit();
    }

    /**
     * Rollback current transaction.
     */
    public function rollback(): void
    {
        $this->database->rollback();
    }

    /**
     * Check if in transaction.
     */
    public function inTransaction(): bool
    {
        return $this->database->isInTransaction();
    }

    /**
     * Get the auth context for this command.
     * @return array<string, mixed>
     */
    public function getAuthContext(): array
    {
        return $this->authContext;
    }

    /**
     * The request's Database — commands that need raw SQL (e.g. the built-in
     * tenant.provision) share the same connection as beginTransaction()/
     * commit(), so their statements join the command's transaction.
     */
    public function database(): Database
    {
        return $this->database;
    }
}
