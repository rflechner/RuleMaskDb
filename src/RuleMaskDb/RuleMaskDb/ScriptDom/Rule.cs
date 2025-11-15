using System.Collections.Immutable;

namespace RuleMaskDb;

public enum GeneratorType
{
    FirstName,
    Name,
    AddressLine,
    City,
    State,
    ZipCode,
    PhoneNumber,
    Email,
    Age,
    DateOfBirth,
    CreditCardNumber,
    CreditCardExpirationDate,
    CreditCardSecurityCode
}

public enum DatabaseType
{
    SqlServer,
    RavenDb,
}

public record Rule(string Table, string Column, string? Mask, GeneratorType? Generator);

public record DatabaseSpecification(DatabaseType DatabaseType, string ConnectionString);

public record ScriptSpecification(DatabaseSpecification Database, ImmutableArray<Rule> Rules);
