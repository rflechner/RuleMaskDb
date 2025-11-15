using Bogus;

namespace RuleMaskDb.Generators;

public class DateOfBirthGenerator(Faker faker) : IDataGenerator
{
    private readonly Faker _faker = faker;

    public Task<object> GenerateValueAsync(CancellationToken cancellationToken = default)
    {
        // Générer une date de naissance réaliste (0-100 ans dans le passé)
        var value = _faker.Date.Past(100, DateTime.Today);
        return Task.FromResult<object>(value.Date);
    }
}
