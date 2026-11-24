<?php

declare(strict_types=1);

namespace ShapeRuntime\Core;

/**
 * Helpers for turning a mask param schema into agent-friendly error hints
 * (`expected`, `example`). Used by ParamResolver and other call sites that
 * need to attach a self-documenting fix-hint to a thrown ApiException.
 *
 * Param schema shape (from MaskRegistry / config):
 *
 *     [ 'id' => ['type' => 'int', 'required' => true],
 *       'tag' => ['type' => 'string'] ]
 *
 * `describe()` and `expected()` produce text descriptions agents can read.
 * `example()` and `examplePayload()` produce concrete sample values agents
 * can paste back as a corrected request.
 */
final class ParamSchema
{
    /**
     * Render one param rule as "type" or "type (required)".
     *
     * @param array<string, mixed> $rule
     */
    public static function describe(array $rule): string
    {
        $type = (string)($rule['type'] ?? 'mixed');
        $required = (bool)($rule['required'] ?? false);
        return $required ? "{$type} (required)" : $type;
    }

    /**
     * Sample value for a single type — chosen to be obviously fake but
     * structurally valid so agents can replace it with a real value.
     */
    public static function example(string $type): mixed
    {
        return match (strtolower($type)) {
            'int', 'integer'                    => 42,
            'float', 'double', 'decimal', 'number' => 3.14,
            'bool', 'boolean'                   => true,
            'array_int'                         => [1, 2, 3],
            'array_string'                      => ['foo', 'bar'],
            'string', 'varchar', 'char', 'text' => 'example',
            default                             => null,
        };
    }

    /**
     * Whole-schema text description, e.g. ['id' => 'int (required)', 'tag' => 'string'].
     *
     * @param array<string, array<string, mixed>> $schema
     * @return array<string, string>
     */
    public static function expected(array $schema): array
    {
        $out = [];
        foreach ($schema as $name => $rule) {
            $out[(string)$name] = self::describe(is_array($rule) ? $rule : []);
        }
        return $out;
    }

    /**
     * Whole-schema sample payload, e.g. ['id' => 42]. Defaults to required
     * fields only — that's the smallest valid object an agent can submit.
     *
     * @param array<string, array<string, mixed>> $schema
     * @return array<string, mixed>
     */
    public static function examplePayload(array $schema, bool $requiredOnly = true): array
    {
        $out = [];
        foreach ($schema as $name => $rule) {
            if (!is_array($rule)) {
                continue;
            }
            if ($requiredOnly && !($rule['required'] ?? false)) {
                continue;
            }
            $out[(string)$name] = self::example((string)($rule['type'] ?? 'mixed'));
        }
        return $out;
    }
}
