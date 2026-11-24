<?php

declare(strict_types=1);

namespace ShapeRuntime\Core;

final class ParamResolver
{
    /**
     * @param array<string, mixed> $incomingParams
     * @param array<string, array<string, mixed>> $schema
     * @param array<string, array{meta:array<string, mixed>, rows:list<list<mixed>>}> $resolvedRefs
     * @return array<string, mixed>
     */
    public function resolve(
        array $incomingParams,
        array $schema,
        array $resolvedRefs,
        ?string $currentRef = null
    ): array {
        $result = [];

        foreach ($schema as $name => $rule) {
            $required = (bool)($rule['required'] ?? false);
            if (!array_key_exists($name, $incomingParams)) {
                if ($required) {
                    throw new ApiException(
                        'PARAM_VALIDATION_ERROR',
                        "Missing required parameter '{$name}'.",
                        400,
                        $currentRef,
                        [
                            'expected' => ParamSchema::expected($schema),
                            'example' => ParamSchema::examplePayload($schema, requiredOnly: true),
                        ]
                    );
                }
                continue;
            }
            $resolved = $this->resolveValue($incomingParams[$name], $resolvedRefs, $currentRef);
            $result[$name] = $this->coerceType($resolved, (string)($rule['type'] ?? 'mixed'), $name, $currentRef);
        }

        foreach ($incomingParams as $name => $_) {
            // Reserved-prefix hygiene (MULTI-TENANCY-PLAN §2.4): the `__`
            // prefix is framework-only (e.g. the baked `:__tenant` scope
            // binding). A client may never supply one, even if a mask
            // happened to declare it — closes the "declared, so sendable" hole.
            if (str_starts_with((string) $name, '__')) {
                throw new ApiException(
                    'PARAM_VALIDATION_ERROR',
                    "Parameter '{$name}' uses the reserved '__' prefix.",
                    400,
                    $currentRef,
                    [
                        'hint' => "Parameters starting with '__' are reserved for framework use (e.g. tenant scoping) and cannot be supplied by clients.",
                    ]
                );
            }
            if (!array_key_exists($name, $schema)) {
                throw new ApiException(
                    'PARAM_VALIDATION_ERROR',
                    "Unknown parameter '{$name}'.",
                    400,
                    $currentRef,
                    [
                        'expected' => ParamSchema::expected($schema),
                        'hint' => $schema === []
                            ? 'This mask takes no parameters.'
                            : 'Allowed parameters: ' . implode(', ', array_keys($schema)) . '.',
                    ]
                );
            }
        }

        return $result;
    }

    /**
     * @param array<string, array{meta:array<string, mixed>, rows:list<list<mixed>>}> $resolvedRefs
     */
    private function resolveValue(mixed $value, array $resolvedRefs, ?string $currentRef): mixed
    {
        if (!is_array($value) || array_is_list($value)) {
            return $value;
        }

        $ref = $value['ref'] ?? null;
        if (!is_string($ref) || $ref === '') {
            return $value;
        }

        $source = $resolvedRefs[$ref] ?? null;
        if ($source === null) {
            throw new ApiException('PARAM_REFERENCE_ERROR', "Reference '{$ref}' not found.", 400, $currentRef);
        }

        /** @var list<string> $columns */
        $columns = $source['meta']['columns'] ?? [];
        $indexByColumn = [];
        foreach ($columns as $i => $columnName) {
            $indexByColumn[$columnName] = $i;
        }

        if (isset($value['value']) && is_string($value['value'])) {
            $column = $value['value'];
            if (!array_key_exists($column, $indexByColumn)) {
                throw new ApiException('PARAM_REFERENCE_ERROR', "Column '{$column}' not found in ref '{$ref}'.", 400, $currentRef);
            }
            $firstRow = $source['rows'][0] ?? null;
            if (!is_array($firstRow)) {
                throw new ApiException('PARAM_REFERENCE_ERROR', "Ref '{$ref}' has no rows for scalar reference.", 400, $currentRef);
            }
            return $firstRow[$indexByColumn[$column]] ?? null;
        }

        if (isset($value['list']) && is_string($value['list'])) {
            $column = $value['list'];
            if (!array_key_exists($column, $indexByColumn)) {
                throw new ApiException('PARAM_REFERENCE_ERROR', "Column '{$column}' not found in ref '{$ref}'.", 400, $currentRef);
            }
            $idx = $indexByColumn[$column];
            $out = [];
            foreach ($source['rows'] as $row) {
                $out[] = $row[$idx] ?? null;
            }
            return $out;
        }

        return $value;
    }

    private function coerceType(mixed $value, string $type, string $paramName, ?string $currentRef): mixed
    {
        return match ($type) {
            'int' => $this->toInt($value, $paramName, $currentRef),
            'float' => $this->toFloat($value, $paramName, $currentRef),
            'string' => $this->toString($value, $paramName, $currentRef),
            'bool' => (bool)$value,
            'array_int' => $this->toArrayInt($value, $paramName, $currentRef),
            'array_string' => $this->toArrayString($value, $paramName, $currentRef),
            default => $value,
        };
    }

    private function toInt(mixed $value, string $paramName, ?string $currentRef): int
    {
        if (is_int($value)) {
            return $value;
        }
        if (is_string($value) && preg_match('/^-?\d+$/', $value) === 1) {
            return (int)$value;
        }
        throw self::typeError($paramName, 'int', $currentRef);
    }

    private function toFloat(mixed $value, string $paramName, ?string $currentRef): float
    {
        if (is_float($value) || is_int($value)) {
            return (float)$value;
        }
        if (is_string($value) && is_numeric($value)) {
            return (float)$value;
        }
        throw self::typeError($paramName, 'float', $currentRef);
    }

    private function toString(mixed $value, string $paramName, ?string $currentRef): string
    {
        if (is_scalar($value)) {
            return (string)$value;
        }
        throw self::typeError($paramName, 'string', $currentRef);
    }

    /** @return list<int> */
    private function toArrayInt(mixed $value, string $paramName, ?string $currentRef): array
    {
        if (!is_array($value)) {
            throw self::typeError($paramName, 'array_int', $currentRef);
        }
        if ($value === []) {
            throw new ApiException(
                'PARAM_VALIDATION_ERROR',
                "Parameter '{$paramName}' cannot be an empty array.",
                400,
                $currentRef,
                [
                    'expected' => [$paramName => 'array_int (non-empty)'],
                    'example' => [$paramName => ParamSchema::example('array_int')],
                ]
            );
        }
        $out = [];
        foreach ($value as $v) {
            $out[] = $this->toInt($v, $paramName, $currentRef);
        }
        return $out;
    }

    /** @return list<string> */
    private function toArrayString(mixed $value, string $paramName, ?string $currentRef): array
    {
        if (!is_array($value)) {
            throw self::typeError($paramName, 'array_string', $currentRef);
        }
        if ($value === []) {
            throw new ApiException(
                'PARAM_VALIDATION_ERROR',
                "Parameter '{$paramName}' cannot be an empty array.",
                400,
                $currentRef,
                [
                    'expected' => [$paramName => 'array_string (non-empty)'],
                    'example' => [$paramName => ParamSchema::example('array_string')],
                ]
            );
        }
        $out = [];
        foreach ($value as $v) {
            $out[] = $this->toString($v, $paramName, $currentRef);
        }
        return $out;
    }

    private static function typeError(string $paramName, string $expectedType, ?string $currentRef): ApiException
    {
        return new ApiException(
            'PARAM_VALIDATION_ERROR',
            "Parameter '{$paramName}' must be {$expectedType}.",
            400,
            $currentRef,
            [
                'expected' => [$paramName => $expectedType],
                'example' => [$paramName => ParamSchema::example($expectedType)],
            ]
        );
    }
}
