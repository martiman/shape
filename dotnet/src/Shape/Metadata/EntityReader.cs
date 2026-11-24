using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Serialization;
using Shape.Attributes;

namespace Shape.Metadata;

public sealed class EntityReader
{
    private readonly NullabilityInfoContext _nullability = new();

    public List<EntityMeta> ReadFromAssembly(Assembly assembly)
    {
        var types = assembly.GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract && type.GetCustomAttribute<EntityAttribute>() != null)
            .ToList();

        return ReadTypes(types);
    }

    public List<EntityMeta> ReadTypes(IEnumerable<Type> types)
    {
        return types
            .Where(type => type.GetCustomAttribute<EntityAttribute>() != null)
            .Select(ReadEntity)
            .ToList();
    }

    private EntityMeta ReadEntity(Type type)
    {
        var entity = type.GetCustomAttribute<EntityAttribute>()!;
        var access = type.GetCustomAttribute<AccessAttribute>();

        var readRoles = access?.Read.Length > 0 ? access.Read : access?.Roles ?? Array.Empty<string>();
        var writeRoles = access?.Write.Length > 0 ? access.Write : access?.Roles ?? Array.Empty<string>();

        // Per-operation split (Create/Update/Delete): resolved eagerly by the
        // action → Write → Roles cascade, but only when at least one action
        // was configured explicitly — an empty resolved list then means
        // "nobody", never "everybody".
        var writeRolesByAction = new Dictionary<string, List<string>>();
        if (access != null && (access.Create.Length > 0 || access.Update.Length > 0 || access.Delete.Length > 0))
        {
            List<string> Resolve(string[] action) =>
                (action.Length > 0 ? action : writeRoles)
                    .Where(role => !string.IsNullOrWhiteSpace(role)).ToList();
            writeRolesByAction["create"] = Resolve(access.Create);
            writeRolesByAction["update"] = Resolve(access.Update);
            writeRolesByAction["delete"] = Resolve(access.Delete);
        }

        var fields = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.GetMethod != null)
            .Select(ReadField)
            .ToList();

        // Auto-augment per-field validators with schema-derived rules. Manual
        // [Validate] attributes win at evaluation time; we just append the
        // auto-derived chain so users get notEmpty + maxLen:N for free on
        // string columns without ever writing them.
        foreach (var field in fields)
        {
            var auto = AutoValidators.FromField(field);
            foreach (var rule in auto)
            {
                if (!field.Validators.Contains(rule))
                    field.Validators.Add(rule);
            }
        }

        var uniques = type.GetCustomAttributes<UniqueAttribute>()
            .Where(u => u.Columns.Length > 0)
            .Select(u => u.Columns.ToList())
            .ToList();

        // [GeneratedId] pre-seeds the manual PK strategy; PkStrategyResolver.Apply
        // validates it (single int-family PK) and lets an explicit per-table
        // PkAssignment override still win.
        var hasGeneratedId = type
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Any(p => p.GetCustomAttribute<GeneratedIdAttribute>() != null);

        return new EntityMeta
        {
            ClrType = type,
            Table = entity.Table,
            Writable = entity.Writable,
            WritableMode = entity.WritableMode,
            PkStrategy = hasGeneratedId ? "manual" : null,
            Description = entity.Description,
            Fields = fields,
            ReadRoles = readRoles.Where(role => !string.IsNullOrWhiteSpace(role)).ToList(),
            WriteRoles = writeRoles.Where(role => !string.IsNullOrWhiteSpace(role)).ToList(),
            WriteRolesByAction = writeRolesByAction,
            SoftDeleteColumn = EntityMeta.DetectSoftDeleteColumn(fields),
            AuditColumns = EntityMeta.DetectAuditColumns(fields),
            TenantColumn = EntityMeta.DetectTenantColumn(fields),
            UniqueConstraints = uniques,
            Hooks = ReadHooks(type)
        };
    }

    /// <summary>
    /// Scan static methods on the entity type for hook attributes
    /// (<c>[BeforeInsert]</c>, <c>[AfterUpdate]</c>, …). Each match is wrapped
    /// in a delegate that takes the standard hook context dictionary, so the
    /// dispatcher can run it without knowing which method backed it.
    /// </summary>
    private static Dictionary<string, List<Action<Dictionary<string, object?>>>> ReadHooks(Type type)
    {
        var hooks = new Dictionary<string, List<Action<Dictionary<string, object?>>>>();

        void Add(string key, MethodInfo method)
        {
            if (!hooks.TryGetValue(key, out var list))
            {
                list = new List<Action<Dictionary<string, object?>>>();
                hooks[key] = list;
            }
            list.Add(ctx => InvokeHookMethod(method, ctx));
        }

        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
        foreach (var m in methods)
        {
            if (m.GetCustomAttribute<BeforeInsertAttribute>() != null) Add("before-insert", m);
            if (m.GetCustomAttribute<BeforeUpdateAttribute>() != null) Add("before-update", m);
            if (m.GetCustomAttribute<BeforeDeleteAttribute>() != null) Add("before-delete", m);
            if (m.GetCustomAttribute<AfterInsertAttribute>() != null) Add("after-insert", m);
            if (m.GetCustomAttribute<AfterUpdateAttribute>() != null) Add("after-update", m);
            if (m.GetCustomAttribute<AfterDeleteAttribute>() != null) Add("after-delete", m);
        }

        return hooks;
    }

    private static void InvokeHookMethod(MethodInfo method, Dictionary<string, object?> ctx)
    {
        var parameters = method.GetParameters();
        object? instance = method.IsStatic ? null : Activator.CreateInstance(method.DeclaringType!);

        if (parameters.Length == 0)
            method.Invoke(instance, Array.Empty<object?>());
        else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(Dictionary<string, object?>))
            method.Invoke(instance, new object?[] { ctx });
        else
            throw new InvalidOperationException(
                $"Hook method '{method.DeclaringType?.Name}.{method.Name}' must take no parameters " +
                "or a single Dictionary<string, object?> parameter.");
    }

    private FieldMeta ReadField(PropertyInfo property)
    {
        var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        var nullability = _nullability.Create(property);
        var isNullable = nullability.ReadState == NullabilityState.Nullable;
        var column = property.GetCustomAttribute<ColumnAttribute>();
        var fk = property.GetCustomAttribute<FkAttribute>();

        var validators = property.GetCustomAttributes<ValidateAttribute>()
            .Select(attribute => attribute.Rule)
            .Where(rule => !string.IsNullOrWhiteSpace(rule))
            .ToList();

        return new FieldMeta
        {
            Name = ResolveColumnName(property),
            ClrType = propertyType,
            ClrTypeName = propertyType.Name,
            DbType = InferDbType(propertyType, column),
            Nullable = isNullable,
            IsPrimary = property.GetCustomAttribute<IdAttribute>() != null,
            IsAutoIncrement = property.GetCustomAttribute<AutoIncrementAttribute>() != null,
            Unsigned = (column?.Unsigned ?? false) || property.GetCustomAttribute<AutoIncrementAttribute>() != null,
            Length = column?.Length > 0 ? column.Length : null,
            Precision = column?.Precision > 0 ? column.Precision : null,
            Scale = column?.Scale > 0 ? column.Scale : null,
            HasDefault = property.GetCustomAttribute<DefaultValueAttribute>() != null,
            DefaultValue = property.GetCustomAttribute<DefaultValueAttribute>()?.Value,
            FkEntityType = fk?.EntityType,
            FkColumn = fk?.Column,
            FkOnDelete = fk?.OnDelete ?? "cascade",
            FkOnUpdate = fk?.OnUpdate ?? "cascade",
            Validators = validators,
            EnumValues = column?.Values is { Length: > 0 } values ? values.ToList() : null,
            Description = column?.Description
            // IsTenantColumn is owned by TenancyResolver.Apply (run for every
            // build): it flags the field only when tenancy is enabled and only
            // for the configured tenancy.column. Hardcoding the literal
            // "tenant_id" here mis-flagged non-tenant apps (column stripped from
            // insert/update, marked read-only) and ignored a renamed column.
        };
    }

    private static string ResolveColumnName(PropertyInfo property)
    {
        if (property.GetCustomAttribute<JsonPropertyNameAttribute>() is { } json)
            return json.Name;

        return ToSnakeCase(property.Name);
    }

    private static string InferDbType(Type propertyType, ColumnAttribute? column)
    {
        if (!string.IsNullOrWhiteSpace(column?.Type))
            return column.Type!;

        if (propertyType == typeof(int) || propertyType == typeof(long) || propertyType == typeof(short))
            return "int";
        if (propertyType == typeof(float) || propertyType == typeof(double) || propertyType == typeof(decimal))
            return "double";
        if (propertyType == typeof(bool))
            return "tinyint";
        if (propertyType == typeof(DateTime))
            return "datetime";
        return "varchar";
    }

    private static string ToSnakeCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        var chars = new List<char>(value.Length + 4);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsUpper(c) && i > 0)
                chars.Add('_');
            chars.Add(char.ToLowerInvariant(c));
        }

        return new string(chars.ToArray());
    }
}
