using Bogus;

namespace RuleMaskDb.Generators;

public class FirstNameGenerator(Faker faker) : IDataGenerator
{
    private readonly Faker _faker = faker;

    public Task<object> GenerateValueAsync(CancellationToken cancellationToken = default)
    {
        var value = _faker.Name.FirstName();
        return Task.FromResult<object>(value);
    }
}