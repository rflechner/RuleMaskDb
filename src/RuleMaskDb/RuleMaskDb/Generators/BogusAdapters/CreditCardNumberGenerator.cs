using Bogus;

namespace RuleMaskDb.Generators;

public class CreditCardNumberGenerator(Faker faker) : IDataGenerator
{
    private readonly Faker _faker = faker;

    public Task<object> GenerateValueAsync(CancellationToken cancellationToken = default)
    {
        var value = _faker.Finance.CreditCardNumber();
        return Task.FromResult<object>(value);
    }
}
