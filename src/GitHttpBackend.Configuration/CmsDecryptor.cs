using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace GitHttpBackend.Configuration;

internal sealed class CmsDecryptor
{
    readonly X509Certificate2Collection _certificates;

    public CmsDecryptor(X509Certificate2Collection certificates)
    {
        _certificates = certificates;
    }

    public static CmsDecryptor FromStore(string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
            return new CmsDecryptor([]);

        var normalized = new string(thumbprint.Where(char.IsAsciiHexDigit).ToArray()).ToUpperInvariant();

        foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
        {
            using var store = new X509Store(StoreName.My, location);
            try
            {
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            }
            catch (CryptographicException)
            {
                continue;
            }

            var found = store.Certificates.Find(X509FindType.FindByThumbprint, normalized, validOnly: false);
            if (found.Count == 0)
                continue;

            if (!found[0].HasPrivateKey)
            {
                throw new InvalidOperationException(
                    $"Certificate {normalized} in {location}\\My has no private key.");
            }

            return new CmsDecryptor(found);
        }

        throw new InvalidOperationException(
            $"Certificate {normalized} was not found in LocalMachine\\My or CurrentUser\\My.");
    }

    public byte[] Decrypt(byte[] armored, string what)
    {
        byte[] der;
        try
        {
            var text = Encoding.ASCII.GetString(armored)
                .Replace("-----BEGIN CMS-----", "", StringComparison.Ordinal)
                .Replace("-----END CMS-----", "", StringComparison.Ordinal);
            der = Convert.FromBase64String(text);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"{what} is not a base64 CMS message.", ex);
        }

        var cms = new EnvelopedCms();
        try
        {
            cms.Decode(der);
            if (_certificates.Count == 0)
                cms.Decrypt();
            else
                cms.Decrypt(_certificates);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                $"{what} could not be decrypted as {Environment.UserDomainName}\\{Environment.UserName}. "
                + "Check that the recipient certificate is installed and that this account can read its private key.",
                ex);
        }

        return cms.ContentInfo.Content;
    }

    public string DecryptString(string armored, string what)
    {
        var text = Encoding.UTF8.GetString(Decrypt(Encoding.ASCII.GetBytes(armored), what));
        return text.TrimStart('﻿').Trim();
    }
}
