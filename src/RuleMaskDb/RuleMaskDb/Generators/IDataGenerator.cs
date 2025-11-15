using RuleMaskDb.ScriptDom;

namespace RuleMaskDb.Generators;

public interface IDataGeneratorFactory
{
    IDataGenerator Create(GeneratorType generatorType);
}

public class BogusDataGeneratorFactory : IDataGeneratorFactory
{
    public IDataGenerator Create(GeneratorType generatorType)
    {
        var faker = new Bogus.Faker();
        return generatorType switch
        {
            GeneratorType.FirstName => new FirstNameGenerator(faker),
            GeneratorType.Name => new NameGenerator(faker),
            GeneratorType.AddressLine => new AddressLineGenerator(faker),
            GeneratorType.City => new CityGenerator(faker),
            GeneratorType.State => new StateGenerator(faker),
            GeneratorType.ZipCode => new ZipCodeGenerator(faker),
            GeneratorType.PhoneNumber => new PhoneNumberGenerator(faker),
            GeneratorType.Email => new EmailGenerator(faker),
            GeneratorType.Age => new AgeGenerator(faker),
            GeneratorType.DateOfBirth => new DateOfBirthGenerator(faker),
            GeneratorType.CreditCardNumber => new CreditCardNumberGenerator(faker),
            GeneratorType.CreditCardExpirationDate => new CreditCardExpirationDateGenerator(faker),
            GeneratorType.CreditCardSecurityCode => new CreditCardSecurityCodeGenerator(faker),
            _ => throw new ArgumentOutOfRangeException(nameof(generatorType), generatorType, null)
        };
    }
}

public interface IDataGenerator
{
    Task<object> GenerateValueAsync(CancellationToken cancellationToken = default);
}