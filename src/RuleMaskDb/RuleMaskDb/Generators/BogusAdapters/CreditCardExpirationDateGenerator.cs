using Bogus;

namespace RuleMaskDb.Generators;

public class CreditCardExpirationDateGenerator(Faker faker) : IDataGenerator
{
    private readonly Faker _faker = faker;

    public Task<object> GenerateValueAsync(CancellationToken cancellationToken = default)
    {
        // Générer une date d'expiration future et formater en MM/yy
        var future = _faker.Date.Future(5);
        var value = future.ToString("MM/yy");
        return Task.FromResult<object>(value);
    }
}
