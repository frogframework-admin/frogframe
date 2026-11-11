namespace Framework.Security;

/// <summary>
/// Reversible encryption service for sensitive credentials (e.g. webhook secrets, IMAP mailbox passwords).
/// </summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedText);
}

/// <summary>
/// Simple in-memory / testing implementation of ISecretProtector using Base64 encoding with a test prefix.
/// Production environments use DataProtectionSecretProtector backed by ASP.NET Core Data Protection.
/// </summary>
public sealed class InMemorySecretProtector : ISecretProtector
{
    private const string Prefix = "sec:dp:";

    public string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        return Prefix + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext));
    }

    public string Unprotect(string protectedText)
    {
        if (string.IsNullOrEmpty(protectedText)) return "";
        if (!protectedText.StartsWith(Prefix, StringComparison.Ordinal)) return protectedText;
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedText[Prefix.Length..]));
    }
}

