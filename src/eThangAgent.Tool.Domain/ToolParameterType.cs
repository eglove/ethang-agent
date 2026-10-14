namespace eThangAgent.ToolDomain;

/// <summary>The value type a tool parameter demands. Names avoid .NET type-name
/// collisions (CA1720): Text/TextArray/WholeNumber/Flag/ObjectValue map to
/// JSON string/array/integer/boolean/object in the advertised schema.</summary>
public enum ToolParameterType { Text, TextArray, WholeNumber, Flag, ObjectValue }
