namespace RuleMaskDb;

public static class Extensions
{
    extension(string)
    {
        public static string operator /(string left, string right)
            => Path.Combine(left, right);

    }
}