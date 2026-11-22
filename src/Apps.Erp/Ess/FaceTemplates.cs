using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Framework.Documents;
using Microsoft.AspNetCore.DataProtection;

namespace Apps.Erp.Ess;

/// <summary>
/// Face templates (embeddings) produced by the ESS app's face model. Stored encrypted with ASP.NET Data Protection
/// (purpose <see cref="Purpose"/>), so a copy of the file store or database alone does not reveal biometric data.
/// The plain template is only returned to the employee's own app through the ESS API.
/// </summary>
public static class FaceTemplates
{
    public const string Purpose = "FrogFrame.Ess.FaceTemplate.v1";
    public const string ContentType = "application/vnd.frogframe.face-template";
    public const int MinDimensions = 32;
    public const int MaxDimensions = 4096;

    /// <summary>Checks the vector (finite numbers, 32–4096 values) and returns its canonical JSON form.</summary>
    public static string Canonical(IReadOnlyList<double>? vector)
    {
        if (vector is null || vector.Count is < MinDimensions or > MaxDimensions)
            throw new ValidationFailedException([new FieldError("template", $"The face template must have {MinDimensions}–{MaxDimensions} numbers")]);
        if (vector.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
            throw new ValidationFailedException([new FieldError("template", "The face template contains invalid numbers")]);
        return "[" + string.Join(",", vector.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + "]";
    }

    public static byte[] Protect(IDataProtectionProvider dp, string canonical) =>
        dp.CreateProtector(Purpose).Protect(Encoding.UTF8.GetBytes(canonical));

    /// <summary>The vector, or null when the bytes were not produced by <see cref="Protect"/> with this installation's keys.</summary>
    public static double[]? Unprotect(IDataProtectionProvider dp, byte[] stored)
    {
        try
        {
            var json = Encoding.UTF8.GetString(dp.CreateProtector(Purpose).Unprotect(stored));
            return JsonSerializer.Deserialize<double[]>(json);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }

    /// <summary>SHA-256 of the canonical template, to compare versions without exposing the template.</summary>
    public static string Fingerprint(string canonical) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
}
