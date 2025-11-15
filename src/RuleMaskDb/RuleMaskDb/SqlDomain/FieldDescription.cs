namespace RuleMaskDb.SqlDomain;

public record FieldDescription(string Path, DateType DataType, bool IsPrimaryKey);