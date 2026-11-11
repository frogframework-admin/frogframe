using Framework.Security;
using Microsoft.AspNetCore.DataProtection;

namespace Framework.Web;

/// <summary>
/// Production secret protector implementing ASP.NET Core Data Protection under purpose "FrogFrame.Automation.Secrets".
/// Encrypts secrets such as webhook signing keys and IMAP mailbox credentials.
/// </summary>
public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    public const string Purpose = "FrogFrame.Automation.Secrets";
    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public string Protect(string plaintext) =>
        string.IsNullOrEmpty(plaintext) ? "" : _protector.Protect(plaintext);

    public string Unprotect(string protectedText) =>
        string.IsNullOrEmpty(protectedText) ? "" : _protector.Unprotect(protectedText);
}

