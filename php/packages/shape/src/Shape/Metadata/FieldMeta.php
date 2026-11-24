<?php

declare(strict_types=1);

namespace Shape\Metadata;

final class FieldMeta
{
    public function __construct(
        public readonly string $name,
        public readonly string $phpType,
        public readonly string $dbType,
        public readonly bool $nullable = false,
        public readonly bool $isPrimary = false,
        public readonly bool $isAutoIncrement = false,
        public readonly bool $unsigned = false,
        public readonly ?int $length = null,
        public readonly ?int $precision = null,
        public readonly ?int $scale = null,
        public readonly bool $hasDefault = false,
        public readonly mixed $default = null,
        public readonly ?string $fkEntity = null,
        public readonly ?string $fkColumn = null,
        public readonly string $fkOnDelete = 'cascade',
        public readonly string $fkOnUpdate = 'cascade',
        public readonly array $validators = [],
        /** @var list<string>|null */
        public readonly ?array $enumValues = null,
        public readonly ?string $description = null,
    ) {}

    public function paramType(): string
    {
        return match ($this->phpType) {
            'int' => 'int',
            'float' => 'float',
            'bool' => 'bool',
            default => 'string',
        };
    }

    public function isFk(): bool
    {
        return $this->fkEntity !== null;
    }
}
