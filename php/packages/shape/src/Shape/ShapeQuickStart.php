<?php

declare(strict_types=1);

namespace Shape;

/**
 * Tiny wrapper returned by ShapeApp::quickStart() so the entry point can be
 * a single chained line:
 *
 *     ShapeApp::quickStart(__DIR__ . '/.env')->serve();
 *
 * The wrapper holds the resolved config and the service name. Call serve()
 * to dispatch through ShapeRouter, or config() for tests / introspection.
 */
final class ShapeQuickStart
{
    /** @param array<string, mixed> $config */
    public function __construct(
        private readonly array $config,
        private readonly string $serviceName = 'shape',
    ) {
    }

    /**
     * Hand off the request to ShapeRouter. Equivalent to writing:
     *
     *     ShapeRouter::run($config, $serviceName);
     */
    public function serve(): void
    {
        ShapeRouter::run($this->config, $this->serviceName);
    }

    /**
     * The fully-resolved config (db, auth, masks, validators, _shape, etc.).
     * Useful in tests or when wiring extra middleware around ShapeRouter.
     *
     * @return array<string, mixed>
     */
    public function config(): array
    {
        return $this->config;
    }

    public function serviceName(): string
    {
        return $this->serviceName;
    }
}
