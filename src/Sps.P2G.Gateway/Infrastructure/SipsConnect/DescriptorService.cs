using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Sps.P2G.Gateway.Domain.Authorities;
using Sps.P2G.Gateway.Domain.Bills;
using Sps.P2G.Gateway.Infrastructure.Security;

namespace Sps.P2G.Gateway.Infrastructure.SipsConnect;

public interface IDescriptorService
{
    DescriptorResult Build(Bill bill, RevenueAccount account);
}

public sealed record DescriptorResult(
    string Descriptor,
    string FullHmac,
    string Version,
    int KeyVersion);

public sealed class DescriptorService(IOptions<P2GOptions> options) : IDescriptorService
{
    public DescriptorResult Build(Bill bill, RevenueAccount account)
    {
        const string version = "P2G1";
        const int keyVersion = 1;
        var dueDate = bill.DueDate?.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
            ?? bill.ExpiresAt.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var invoice = Shorten(bill.ExternalInvoiceId, 24);
        var payerReference = Shorten(bill.TaxpayerReference, 18);
        var amount = bill.Amount.ToString("0.00", CultureInfo.InvariantCulture);
        var hmacInput = $"{bill.Id}|{bill.UPR}|{bill.ExternalInvoiceId}|{amount}|{bill.Currency}|{account.Id}|{bill.ExpiresAt:O}|{version}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(options.Value.DescriptorHmacSecret));
        var fullHmac = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(hmacInput)));
        var shortMac = fullHmac[..8];
        var descriptor = $"{version}|{Shorten(bill.MdaCode, 10)}|{Shorten(bill.ServiceCode, 10)}|{invoice}|{bill.Currency}|{amount}|{dueDate}|{payerReference}|{shortMac}";

        if (descriptor.Length > 140)
        {
            invoice = Shorten(bill.UPR, 20);
            descriptor = $"{version}|{Shorten(bill.MdaCode, 8)}|{Shorten(bill.ServiceCode, 8)}|{invoice}|{bill.Currency}|{amount}|{dueDate}|{Shorten(payerReference, 12)}|{shortMac}";
        }

        if (descriptor.Length > 140)
        {
            throw new InvalidOperationException("P2G descriptor exceeds 140 characters.");
        }

        return new DescriptorResult(descriptor, fullHmac, version, keyVersion);
    }

    private static string Shorten(string value, int maxLength)
    {
        value = value.Trim().Replace('|', '-');
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
