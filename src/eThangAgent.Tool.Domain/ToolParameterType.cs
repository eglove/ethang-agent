namespace eThangAgent.ToolDomain;

/// <summary>The value type a tool parameter demands. Names avoid .NET type-name
///     collisions (CA1720): Text/TextArray/WholeNumber/Flag/ObjectValue map to
///     JSON string/array/integer/boolean/object, and ObjectArray maps to an array
///     whose `items` are objects (a heterogeneous shape the tool's own parser
///     validates, e.g. the edit tool's hunks).</summary>
public enum ToolParameterType { Text, TextArray, WholeNumber, Flag, ObjectValue, ObjectArray }
