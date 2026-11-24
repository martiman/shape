namespace Shape.Attributes;

/// <summary>
/// Marks a method on an <c>[Entity]</c>-tagged class as a lifecycle hook
/// invoked before the SQL for the matching action runs. The method is called
/// with a <c>Dictionary&lt;string, object?&gt;</c> carrying <c>table</c>,
/// <c>action</c>, <c>data</c>, and <c>filter</c> — same shape as the
/// dictionary-based hooks declared on <c>ValidatorConfig.Hooks</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class BeforeInsertAttribute : Attribute { }

/// <inheritdoc cref="BeforeInsertAttribute"/>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class BeforeUpdateAttribute : Attribute { }

/// <inheritdoc cref="BeforeInsertAttribute"/>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class BeforeDeleteAttribute : Attribute { }

/// <summary>
/// Marks a method on an <c>[Entity]</c>-tagged class as a lifecycle hook
/// invoked after a successful insert. Receives a dictionary carrying
/// <c>table</c>, <c>action</c>, and <c>affected</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AfterInsertAttribute : Attribute { }

/// <inheritdoc cref="AfterInsertAttribute"/>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AfterUpdateAttribute : Attribute { }

/// <inheritdoc cref="AfterInsertAttribute"/>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AfterDeleteAttribute : Attribute { }
