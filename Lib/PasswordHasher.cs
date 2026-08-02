namespace OrderManager.Backend.Lib;

public static class PasswordHasher
{
    private const int WorkFactor = 12;

    public static string Hash(string plainText) => BCrypt.Net.BCrypt.HashPassword(plainText, workFactor: WorkFactor);

    public static bool Verify(string plainText, string hash) => BCrypt.Net.BCrypt.Verify(plainText, hash);
}
