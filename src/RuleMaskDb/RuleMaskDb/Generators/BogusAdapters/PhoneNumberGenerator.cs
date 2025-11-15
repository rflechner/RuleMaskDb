using Bogus;

namespace RuleMaskDb.Generators;

public class PhoneNumberGenerator(Faker faker) : IDataGenerator
{
    private readonly Faker _faker = faker;

    public Task<object> GenerateValueAsync(CancellationToken cancellationToken = default)
    {
        var value = _faker.Phone.PhoneNumber();
        return Task.FromResult<object>(value);
    }
}
