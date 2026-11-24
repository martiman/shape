<?php

declare(strict_types=1);

namespace Shape\Metadata;

use ReflectionClass;
use ReflectionProperty;
use ReflectionNamedType;
use Shape\Attribute\Entity;
use Shape\Attribute\Column;
use Shape\Attribute\Id;
use Shape\Attribute\AutoIncrement;
use Shape\Attribute\FK;
use Shape\Attribute\Validate;
use Shape\Attribute\Access;
use Shape\Attribute\Unique;

final class EntityReader
{
    /** @var array<string, EntityMeta> class name → metadata */
    private array $entities = [];

    public function __construct(private readonly bool $autoValidators = true)
    {
    }

    /**
     * Scan a directory for entity classes and read their metadata.
     */
    public function scanDirectory(string $directory): void
    {
        $files = glob($directory . '/*.php');
        if ($files === false || $files === []) {
            return;
        }

        $classesBefore = get_declared_classes();

        foreach ($files as $file) {
            require_once $file;
        }

        $classesAfter = get_declared_classes();
        $newClasses = array_diff($classesAfter, $classesBefore);

        foreach ($newClasses as $class) {
            $ref = new ReflectionClass($class);
            $entityAttrs = $ref->getAttributes(Entity::class);
            if ($entityAttrs === []) {
                continue;
            }

            $entity = $entityAttrs[0]->newInstance();
            $this->entities[$class] = $this->readEntity($ref, $entity);
        }
    }

    /** @return array<string, EntityMeta> */
    public function getEntities(): array
    {
        return $this->entities;
    }

    public function findByTable(string $table): ?EntityMeta
    {
        foreach ($this->entities as $meta) {
            if ($meta->table === $table) {
                return $meta;
            }
        }
        return null;
    }

    private function readEntity(ReflectionClass $ref, Entity $entityAttr): EntityMeta
    {
        $accessAttrs = $ref->getAttributes(Access::class);
        $access = $accessAttrs !== [] ? $accessAttrs[0]->newInstance() : null;

        $readRoles = [];
        $writeRoles = [];
        $writeRolesByAction = [];
        if ($access !== null) {
            $readRoles = $access->read !== [] ? $access->read : $access->roles;
            $writeRoles = $access->write !== [] ? $access->write : $access->roles;
            // Per-operation split (create/update/delete): resolved eagerly by
            // the action → write → roles cascade, but only when at least one
            // action was configured explicitly — an empty resolved list then
            // means "nobody", never "everybody".
            if ($access->create !== [] || $access->update !== [] || $access->delete !== []) {
                $writeRolesByAction = [
                    'create' => $access->create !== [] ? $access->create : $writeRoles,
                    'update' => $access->update !== [] ? $access->update : $writeRoles,
                    'delete' => $access->delete !== [] ? $access->delete : $writeRoles,
                ];
            }
        }

        $fields = [];
        foreach ($ref->getProperties(ReflectionProperty::IS_PUBLIC) as $prop) {
            $fields[] = $this->readField($prop);
        }

        $uniqueConstraints = [];
        foreach ($ref->getAttributes(Unique::class) as $attr) {
            $unique = $attr->newInstance();
            $columns = $this->normalizeStringList($unique->columns);
            if ($columns !== []) {
                $uniqueConstraints[] = $columns;
            }
        }

        return new EntityMeta(
            class: $ref->getName(),
            table: $entityAttr->table,
            writable: $entityAttr->writable,
            fields: $fields,
            readRoles: $readRoles,
            writeRoles: $writeRoles,
            softDeleteColumn: EntityMeta::detectSoftDeleteColumn($fields),
            auditColumns: EntityMeta::detectAuditColumns($fields),
            uniqueConstraints: $uniqueConstraints,
            description: $entityAttr->description,
            writableMode: $entityAttr->writableMode,
            writeRolesByAction: $writeRolesByAction,
        );
    }

    private function readField(ReflectionProperty $prop): FieldMeta
    {
        $name = $prop->getName();

        // PHP type
        $type = $prop->getType();
        $phpType = 'string';
        $nullable = false;
        if ($type instanceof ReflectionNamedType) {
            $phpType = $type->getName();
            $nullable = $type->allowsNull();
        }

        // Column attribute
        $columnAttrs = $prop->getAttributes(Column::class);
        $column = $columnAttrs !== [] ? $columnAttrs[0]->newInstance() : null;

        // DB type inference
        $dbType = $this->inferDbType($phpType, $column);

        // Id + AutoIncrement
        $isId = $prop->getAttributes(Id::class) !== [];
        $isAutoIncrement = $prop->getAttributes(AutoIncrement::class) !== [];

        // FK
        $fkAttrs = $prop->getAttributes(FK::class);
        $fk = $fkAttrs !== [] ? $fkAttrs[0]->newInstance() : null;

        // Default value from PHP property (needed by auto-validator derivation below)
        $hasDefault = $prop->hasDefaultValue();
        $default = $hasDefault ? $prop->getDefaultValue() : null;

        // Validators: auto-derived from schema metadata + #[Validate] attributes
        $autoRules = $this->autoValidators ? AutoValidators::fromField([
            'dbType' => $dbType,
            'nullable' => $nullable,
            'isAutoIncrement' => $isAutoIncrement,
            'hasDefault' => $hasDefault,
            'length' => $column?->length,
        ]) : [];
        $validateAttrs = $prop->getAttributes(Validate::class);
        $manualRules = [];
        $enumValues = $this->normalizeStringList($column->values ?? null);
        foreach ($validateAttrs as $attr) {
            $validate = $attr->newInstance();
            $rule = trim($validate->rule);
            if ($rule === '') {
                continue;
            }

            if ($rule === 'in') {
                $enumValues = array_values(array_unique(array_merge(
                    $enumValues,
                    $this->normalizeStringList($validate->values),
                )));
                continue;
            }

            $manualRules[] = $rule;
        }
        if ($enumValues !== []) {
            $manualRules[] = 'in:' . implode(',', $enumValues);
        }
        $validators = array_merge($autoRules, $manualRules);

        // Unsigned: explicit via Column, or implicit for AutoIncrement
        $unsigned = ($column !== null && $column->unsigned) || $isAutoIncrement;

        return new FieldMeta(
            name: $name,
            phpType: $phpType,
            dbType: $dbType,
            nullable: $nullable,
            isPrimary: $isId,
            isAutoIncrement: $isAutoIncrement,
            unsigned: $unsigned,
            length: $column?->length,
            precision: $column?->precision,
            scale: $column?->scale,
            hasDefault: $hasDefault,
            default: $default,
            fkEntity: $fk?->entity,
            fkColumn: $fk?->column,
            fkOnDelete: $fk?->onDelete ?? 'cascade',
            fkOnUpdate: $fk?->onUpdate ?? 'cascade',
            validators: $validators,
        );
    }

    private function inferDbType(string $phpType, ?Column $column): string
    {
        if ($column !== null && $column->type !== null) {
            return $column->type;
        }

        return match ($phpType) {
            'int' => 'int',
            'float' => 'double',
            'bool' => 'tinyint',
            'string' => 'varchar',
            default => 'varchar',
        };
    }

    /**
     * @param array<int, mixed>|null $values
     * @return list<string>
     */
    private function normalizeStringList(?array $values): array
    {
        if ($values === null || $values === []) {
            return [];
        }

        $normalized = [];
        foreach ($values as $value) {
            if (is_string($value)) {
                $trimmed = trim($value);
                if ($trimmed !== '') {
                    $normalized[] = $trimmed;
                }
                continue;
            }

            if (is_int($value) || is_float($value)) {
                $normalized[] = (string)$value;
                continue;
            }
        }

        return array_values(array_unique($normalized));
    }
}
