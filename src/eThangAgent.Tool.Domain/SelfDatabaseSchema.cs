namespace eThangAgent.ToolDomain;

/// <summary>Structure of the agent's own app database as <c>db_schema</c> reports it:
///     the migration version and every visible table and view.</summary>
public sealed record SelfDatabaseSchema(int SchemaVersion, IReadOnlyList<SchemaObject> Objects);

/// <summary>One table or view. <see cref="RowCount"/> is null when counts were not
///     requested (or could not be taken).</summary>
public sealed record SchemaObject(
    string Name,
    bool IsView,
    long? RowCount,
    IReadOnlyList<SchemaColumn> Columns,
    IReadOnlyList<SchemaIndex> Indexes);

/// <summary>One column as declared. <see cref="DefaultValue"/> is the declared default
///     expression text, or null when the column has none.</summary>
public sealed record SchemaColumn(string Name, string Type, bool NotNull, bool IsPrimaryKey, string? DefaultValue);

/// <summary>One index over its table, including the implicit autoindexes that back
///     PRIMARY KEY and UNIQUE constraints (a multi-column UNIQUE constraint is not
///     visible anywhere else).</summary>
public sealed record SchemaIndex(string Name, bool IsUnique, IReadOnlyList<string> Columns);
