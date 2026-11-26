<?php

declare(strict_types=1);

namespace ShapeRuntime\Core;

/**
 * Compiles an OData `$filter` expression into a parameterised SQL condition.
 *
 * The condition is applied by BatchExecutor *inside* the query (before LIMIT /
 * OFFSET and before COUNT), so paging and `$count` see only the matching rows.
 * Evaluating the filter in PHP after pagination (the previous behaviour) silently
 * dropped matches beyond the first page and left `$count` unfiltered.
 *
 * Grammar (precedence: not > and > or; parentheses allowed):
 *   expr       := or
 *   or         := and ('or' and)*
 *   and        := not ('and' not)*
 *   not        := 'not' not | primary
 *   primary    := '(' expr ')' | func | comparison
 *   func       := ('contains' | 'startswith' | 'endswith') '(' column ',' string ')'
 *   comparison := column ('eq' | 'ne' | 'gt' | 'ge' | 'lt' | 'le') literal
 *   literal    := 'text' | number | true | false | null | 2026-11-20 | 2026-11-20T10:00:00Z
 *
 * Column names are plain identifiers and are quoted through the supplied callback;
 * every value is bound as a parameter (names start with `__f`, a prefix clients
 * cannot send), so nothing from the expression is ever concatenated into SQL.
 */
final class ODataFilter
{
    public const MAX_LENGTH = 2000;
    private const MAX_DEPTH = 20;
    private const MAX_CONDITIONS = 50;

    private const COMPARE = ['eq' => '=', 'ne' => '<>', 'gt' => '>', 'ge' => '>=', 'lt' => '<', 'le' => '<='];
    private const FUNCS = ['contains', 'startswith', 'endswith'];

    /** @var list<array{t:string, v:string}> */
    private array $tokens = [];
    private int $pos = 0;
    private int $conditions = 0;
    /** @var array<string, mixed> */
    private array $params = [];
    /** @var list<string> */
    private array $columns = [];
    /** @var callable(string):string */
    private $quote;

    /**
     * @param callable(string):string $quoteIdentifier
     * @return array{sql:string, params:array<string, mixed>, columns:list<string>}
     */
    public static function compile(string $filter, callable $quoteIdentifier, ?string $ref = null): array
    {
        $self = new self();
        $self->quote = $quoteIdentifier;
        try {
            if (strlen($filter) > self::MAX_LENGTH) {
                throw new \InvalidArgumentException('the expression is longer than ' . self::MAX_LENGTH . ' characters');
            }
            $self->tokens = self::tokenize($filter);
            if ($self->tokens === []) {
                throw new \InvalidArgumentException('the expression is empty');
            }
            $sql = $self->parseOr(0);
            if ($self->pos < count($self->tokens)) {
                throw new \InvalidArgumentException("unexpected '" . $self->tokens[$self->pos]['v'] . "'");
            }
        } catch (\InvalidArgumentException $e) {
            throw new ApiException(
                'QUERY_VALIDATION_ERROR',
                'Invalid $filter: ' . $e->getMessage() . '.',
                400,
                $ref,
                [
                    'expected' => "column op value, combined with and / or / not and parentheses; ops: eq ne gt ge lt le; functions: contains(col,'x') startswith(col,'x') endswith(col,'x')",
                    'example' => "seconds ge 3600 and (project_id eq 2 or contains(description,'review'))",
                    'hint' => "Strings use single quotes ('it''s'), dates are 2026-11-20 or 2026-11-20T10:00:00, null is written null.",
                ]
            );
        }

        return ['sql' => $sql, 'params' => $self->params, 'columns' => array_values(array_unique($self->columns))];
    }

    /** @return list<array{t:string, v:string}> t = 'op' ( ) , | 's' string | 'w' word */
    private static function tokenize(string $s): array
    {
        $out = [];
        $n = strlen($s);
        $i = 0;
        while ($i < $n) {
            $c = $s[$i];
            if (ctype_space($c)) {
                $i++;
                continue;
            }
            if ($c === '(' || $c === ')' || $c === ',') {
                $out[] = ['t' => $c, 'v' => $c];
                $i++;
                continue;
            }
            if ($c === "'") {
                $i++;
                $buf = '';
                $closed = false;
                while ($i < $n) {
                    if ($s[$i] === "'") {
                        if ($i + 1 < $n && $s[$i + 1] === "'") { // '' → literal quote
                            $buf .= "'";
                            $i += 2;
                            continue;
                        }
                        $closed = true;
                        $i++;
                        break;
                    }
                    $buf .= $s[$i];
                    $i++;
                }
                if (!$closed) {
                    throw new \InvalidArgumentException('a string literal is not closed');
                }
                $out[] = ['t' => 's', 'v' => $buf];
                continue;
            }
            if (preg_match('/\G[A-Za-z0-9_.:+\-]+/', $s, $m, 0, $i) === 1) {
                $out[] = ['t' => 'w', 'v' => $m[0]];
                $i += strlen($m[0]);
                continue;
            }
            throw new \InvalidArgumentException("unexpected character '{$c}'");
        }
        return $out;
    }

    private function peekWord(): ?string
    {
        $t = $this->tokens[$this->pos] ?? null;
        return $t !== null && $t['t'] === 'w' ? strtolower($t['v']) : null;
    }

    private function parseOr(int $depth): string
    {
        $this->guardDepth($depth);
        $parts = [$this->parseAnd($depth)];
        while ($this->peekWord() === 'or') {
            $this->pos++;
            $parts[] = $this->parseAnd($depth);
        }
        return count($parts) === 1 ? $parts[0] : '(' . implode(' OR ', $parts) . ')';
    }

    private function parseAnd(int $depth): string
    {
        $parts = [$this->parseNot($depth)];
        while ($this->peekWord() === 'and') {
            $this->pos++;
            $parts[] = $this->parseNot($depth);
        }
        return count($parts) === 1 ? $parts[0] : '(' . implode(' AND ', $parts) . ')';
    }

    private function parseNot(int $depth): string
    {
        $this->guardDepth($depth);
        if ($this->peekWord() === 'not') {
            $this->pos++;
            return '(NOT ' . $this->parseNot($depth + 1) . ')';
        }
        return $this->parsePrimary($depth);
    }

    private function parsePrimary(int $depth): string
    {
        $t = $this->tokens[$this->pos] ?? null;
        if ($t === null) {
            throw new \InvalidArgumentException('the expression ends unexpectedly');
        }
        if ($t['t'] === '(') {
            $this->pos++;
            $sql = $this->parseOr($depth + 1);
            $this->expect(')');
            return $sql;
        }
        if ($t['t'] !== 'w') {
            throw new \InvalidArgumentException("unexpected '" . $t['v'] . "'");
        }

        $word = strtolower($t['v']);
        $next = $this->tokens[$this->pos + 1] ?? null;
        if (in_array($word, self::FUNCS, true) && $next !== null && $next['t'] === '(') {
            return $this->parseFunction($word);
        }

        $this->countCondition();
        $col = $this->column($t['v']);
        $this->pos++;
        $opWord = $this->peekWord();
        if ($opWord === null || !isset(self::COMPARE[$opWord])) {
            throw new \InvalidArgumentException("expected an operator (eq, ne, gt, ge, lt, le) after '{$t['v']}'");
        }
        $this->pos++;
        $value = $this->literal();

        if ($value === null) {
            if ($opWord === 'eq') {
                return "{$col} IS NULL";
            }
            if ($opWord === 'ne') {
                return "{$col} IS NOT NULL";
            }
            throw new \InvalidArgumentException("null can only be compared with eq or ne ('{$t['v']}')");
        }
        return $col . ' ' . self::COMPARE[$opWord] . ' ' . $this->bind($value);
    }

    private function parseFunction(string $fn): string
    {
        $this->countCondition();
        $this->pos += 2; // name + '('
        $colTok = $this->tokens[$this->pos] ?? null;
        if ($colTok === null || $colTok['t'] !== 'w') {
            throw new \InvalidArgumentException("{$fn}() needs a column name first");
        }
        $col = $this->column($colTok['v']);
        $this->pos++;
        $this->expect(',');
        $valTok = $this->tokens[$this->pos] ?? null;
        if ($valTok === null || $valTok['t'] !== 's') {
            throw new \InvalidArgumentException("{$fn}() needs a 'quoted string' as the second argument");
        }
        $this->pos++;
        $this->expect(')');

        $esc = str_replace(['!', '%', '_'], ['!!', '!%', '!_'], $valTok['v']);
        $pattern = match ($fn) {
            'contains' => '%' . $esc . '%',
            'startswith' => $esc . '%',
            default => '%' . $esc,
        };
        return "{$col} LIKE " . $this->bind($pattern) . " ESCAPE '!'";
    }

    /** @return string|int|float|bool|null */
    private function literal(): string|int|float|bool|null
    {
        $t = $this->tokens[$this->pos] ?? null;
        if ($t === null) {
            throw new \InvalidArgumentException('a value is missing after the operator');
        }
        $this->pos++;
        if ($t['t'] === 's') {
            return $t['v'];
        }
        if ($t['t'] !== 'w') {
            throw new \InvalidArgumentException("unexpected '" . $t['v'] . "' where a value was expected");
        }
        $v = $t['v'];
        $lower = strtolower($v);
        if ($lower === 'null') {
            return null;
        }
        if ($lower === 'true') {
            return 1;
        }
        if ($lower === 'false') {
            return 0;
        }
        if (preg_match('/^-?\d+$/', $v) === 1 && strlen($v) < 18) {
            return (int)$v;
        }
        if (preg_match('/^-?\d+\.\d+$/', $v) === 1) {
            return (float)$v;
        }
        if (preg_match('/^(\d{4}-\d{2}-\d{2})(?:[T ](\d{2}:\d{2})(?::(\d{2})(?:\.\d+)?)?(?:Z|[+-]\d{2}:?\d{2})?)?$/i', $v, $m) === 1) {
            return isset($m[2]) ? $m[1] . ' ' . $m[2] . ':' . ($m[3] ?? '00') : $m[1];
        }
        throw new \InvalidArgumentException("cannot read the value '{$v}' (put text in single quotes)");
    }

    private function column(string $name): string
    {
        if (preg_match('/^[A-Za-z_][A-Za-z0-9_]{0,63}$/', $name) !== 1) {
            throw new \InvalidArgumentException("'{$name}' is not a valid column name");
        }
        $this->columns[] = $name;
        return ($this->quote)($name);
    }

    /** @param string|int|float|bool $value */
    private function bind(string|int|float|bool $value): string
    {
        $key = '__f' . count($this->params);
        $this->params[$key] = $value;
        return ':' . $key;
    }

    private function expect(string $tok): void
    {
        $t = $this->tokens[$this->pos] ?? null;
        if ($t === null || $t['t'] !== $tok) {
            throw new \InvalidArgumentException("expected '{$tok}'");
        }
        $this->pos++;
    }

    private function guardDepth(int $depth): void
    {
        if ($depth > self::MAX_DEPTH) {
            throw new \InvalidArgumentException('the expression is nested too deeply');
        }
    }

    private function countCondition(): void
    {
        if (++$this->conditions > self::MAX_CONDITIONS) {
            throw new \InvalidArgumentException('too many conditions (maximum ' . self::MAX_CONDITIONS . ')');
        }
    }
}
