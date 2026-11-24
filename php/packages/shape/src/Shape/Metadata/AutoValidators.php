<?php

declare(strict_types=1);

namespace Shape\Metadata;

/**
 * Derives validator rules from column metadata so devs do not have to
 * repeat what the schema already encodes.
 *
 *   NOT NULL string column (no default, not auto-increment) → 'notEmpty'
 *   VARCHAR(N) / CHAR(N)                                    → "maxLen:N"
 *
 * Manual validators from app.php still apply on top — they extend the
 * auto-derived chain rather than replacing it.
 */
final class AutoValidators
{
    private const STRING_TYPES = [
        'varchar', 'char', 'text', 'tinytext', 'mediumtext', 'longtext',
        'character', 'character varying',
        // SQL Server (raw types, in case unnormalized names reach here).
        // `nvarchar(max)` reports CHARACTER_MAXIMUM_LENGTH = -1, which the
        // `$length > 0` guard below already treats as unbounded (no maxLen).
        'nvarchar', 'nchar', 'ntext',
    ];

    /**
     * @param array{type:string,nullable:bool,auto_increment:bool,has_default:bool,length:?int} $col
     * @return list<string>
     */
    public static function fromColumn(array $col): array
    {
        $rules = [];
        $type = strtolower((string)$col['type']);
        $isString = in_array($type, self::STRING_TYPES, true);

        if (!$isString) {
            return $rules;
        }

        $required = !($col['nullable'] ?? false)
            && !($col['auto_increment'] ?? false)
            && !($col['has_default'] ?? false);

        if ($required) {
            $rules[] = 'notEmpty';
        }

        $length = $col['length'] ?? null;
        if (is_int($length) && $length > 0) {
            $rules[] = 'maxLen:' . $length;
        }

        return $rules;
    }

    /**
     * Same shape as fromColumn() but for entity-first mode where the source
     * data is reflection on a property: dbType, nullable, hasDefault, length,
     * isAutoIncrement.
     *
     * @param array{dbType:string,nullable:bool,isAutoIncrement:bool,hasDefault:bool,length:?int} $field
     * @return list<string>
     */
    public static function fromField(array $field): array
    {
        return self::fromColumn([
            'type' => $field['dbType'],
            'nullable' => $field['nullable'],
            'auto_increment' => $field['isAutoIncrement'],
            'has_default' => $field['hasDefault'],
            'length' => $field['length'],
        ]);
    }
}
