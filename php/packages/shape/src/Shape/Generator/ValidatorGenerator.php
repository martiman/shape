<?php

declare(strict_types=1);

namespace Shape\Generator;

use Shape\Metadata\EntityMeta;

final class ValidatorGenerator
{
    /**
     * Generate ShapeRuntime validator config from entity metadata.
     *
     * @param array<string, EntityMeta> $entities
     * @return array<string, array<string, mixed>>
     */
    public function generate(array $entities): array
    {
        $config = [];
        foreach ($entities as $meta) {
            $fieldValidators = $this->buildFieldValidators($meta);
            if ($fieldValidators === []) {
                continue;
            }

            foreach (['insert', 'update'] as $action) {
                $key = $meta->table . '.' . $action;
                $config[$key] = ['validators' => $fieldValidators];
            }
        }
        return $config;
    }

    /** @return array<string, callable> */
    private function buildFieldValidators(EntityMeta $meta): array
    {
        $validators = [];
        foreach ($meta->fields as $field) {
            if ($field->validators === []) {
                continue;
            }

            $checks = [];
            foreach ($field->validators as $rule) {
                $check = self::ruleToCheck($rule);
                if ($check !== null) {
                    $checks[] = $check;
                }
            }

            if ($checks === []) {
                continue;
            }

            $fieldName = $field->name;
            $validators[$fieldName] = static function (mixed $value) use ($checks, $fieldName): ?string {
                foreach ($checks as [$fn, $message]) {
                    if (!$fn($value)) {
                        return str_replace('{field}', $fieldName, $message);
                    }
                }
                return null;
            };
        }
        return $validators;
    }

    /** @return array{0: callable, 1: string}|null */
    private static function ruleToCheck(string $rule): ?array
    {
        if (str_starts_with($rule, 'maxLen:')) {
            $n = (int)substr($rule, 7);
            if ($n <= 0) {
                return null;
            }
            return [
                static fn(mixed $v): bool => !is_string($v) || mb_strlen($v) <= $n,
                "{field} exceeds maximum length of {$n}",
            ];
        }

        if (str_starts_with($rule, 'in:')) {
            $allowed = array_values(array_unique(array_filter(
                array_map('trim', explode(',', substr($rule, 3))),
                static fn(string $value): bool => $value !== ''
            )));
            if ($allowed === []) {
                return null;
            }

            return [
                static fn(mixed $v): bool => $v === null || (is_string($v) && in_array($v, $allowed, true)),
                '{field} must be one of: ' . implode(', ', $allowed),
            ];
        }

        return match ($rule) {
            'positive' => [
                static fn(mixed $v): bool => is_numeric($v) && (float)$v > 0,
                '{field} must be positive',
            ],
            'nonNegative' => [
                static fn(mixed $v): bool => is_numeric($v) && (float)$v >= 0,
                '{field} must be non-negative',
            ],
            'notEmpty' => [
                static fn(mixed $v): bool => is_string($v) && trim($v) !== '',
                '{field} is required',
            ],
            'email' => [
                static fn(mixed $v): bool => is_string($v) && filter_var($v, FILTER_VALIDATE_EMAIL) !== false,
                '{field} must be a valid email',
            ],
            default => null,
        };
    }
}
