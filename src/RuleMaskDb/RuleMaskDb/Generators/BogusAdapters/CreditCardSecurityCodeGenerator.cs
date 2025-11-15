using Bogus;

namespace RuleMaskDb.Generators;

public class CreditCardSecurityCodeGenerator(Faker faker) : IDataGenerator
{
    private readonly Faker _faker = faker;

    public Task<object> GenerateValueAsync(CancellationToken cancellationToken = default)
    {
        var value = _faker.Finance.CreditCardCvv();
        return Task.FromResult<object>(value);
    }
}
