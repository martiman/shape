<?php

declare(strict_types=1);

namespace ShapeRuntime\Core;

use Throwable;

/**
 * Manages field validators and mutation hooks.
 */
final class ValidatorRegistry
{
    /** @param array<string, array<string, mixed>> $config */
    public function __construct(private readonly array $config = [])
    {
    }

    /**
     * Get validators for a table/action combination.
     * @return array<string, callable>
     */
    public function getValidators(string $table, string $action): array
    {
        $key = $table . '.' . $action;
        $config = $this->config[$key] ?? [];
        return is_array($config['validators'] ?? null) ? $config['validators'] : [];
    }

    /**
     * Get before-hook for a table/action combination.
     */
    public function getBeforeHook(string $table, string $action): ?callable
    {
        $key = $table . '.' . $action;
        $config = $this->config[$key] ?? [];
        $hooks = is_array($config['hooks'] ?? null) ? $config['hooks'] : [];
        $beforeHook = $hooks['before'] ?? null;
        
        if ($beforeHook === null) {
            return null;
        }

        if (is_callable($beforeHook)) {
            return $beforeHook;
        }

        if (is_string($beforeHook)) {
            // Support hook names that map to static methods or callables
            return null; // Return null to signal that this should be resolved by caller
        }

        return null;
    }

    /**
     * Get after-hook for a table/action combination.
     */
    public function getAfterHook(string $table, string $action): ?callable
    {
        $key = $table . '.' . $action;
        $config = $this->config[$key] ?? [];
        $hooks = is_array($config['hooks'] ?? null) ? $config['hooks'] : [];
        $afterHook = $hooks['after'] ?? null;
        
        if ($afterHook === null) {
            return null;
        }

        if (is_callable($afterHook)) {
            return $afterHook;
        }

        if (is_string($afterHook)) {
            // Support hook names that map to static methods or callables
            return null; // Return null to signal that this should be resolved by caller
        }

        return null;
    }

    /**
     * Validate a field value against registered validators.
     * @return ?string Error message if validation fails, null if valid
     */
    public function validate(string $table, string $action, string $field, mixed $value): ?string
    {
        $validators = $this->getValidators($table, $action);
        if (!isset($validators[$field])) {
            return null; // No validator registered
        }

        $validator = $validators[$field];
        if (!is_callable($validator)) {
            return null;
        }

        try {
            $error = $validator($value);
            return is_string($error) ? $error : null;
        } catch (Throwable $e) {
            return 'Validator error: ' . $e->getMessage();
        }
    }
}
