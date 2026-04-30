using System.Globalization;
using System.Xml.Linq;

namespace QBBridge.Service.Soap;

public sealed record QbInvoice(
    string RefNumber,
    string? TxnID,
    DateTime? TxnDate,
    string? CustomerFullName,
    decimal? Subtotal,
    decimal? BalanceRemaining,
    decimal? AppliedAmount,
    bool IsPaid,
    DateTime? ModifiedAt);

public sealed record QbPayment(
    string? TxnID,
    DateTime? TxnDate,
    string? CustomerFullName,
    decimal? TotalAmount,
    decimal? UnusedPayment,
    IReadOnlyList<QbPaymentAppliedToInvoice> AppliedTo);

public sealed record QbPaymentAppliedToInvoice(
    string InvoiceRefNumber,
    string? InvoiceTxnID,
    decimal AmountApplied,
    decimal? BalanceRemaining);

/// <summary>
/// Result of an *AddRq → *AddRs round-trip. RequestId is the qbXML requestID
/// attribute we set when building the request; we use it to look up which
/// InTime row to ack when the response comes back.
/// </summary>
public sealed record QbAddResult(
    int? RequestId,
    string Verb,           // "CustomerAdd" | "VendorAdd" | etc
    bool Ok,
    string? ListId,        // populated on Ok=true
    string? FullName,      // populated on Ok=true
    string? Error);        // populated on Ok=false (statusMessage)

public sealed record ParseResult(
    IReadOnlyList<QbInvoice> Invoices,
    IReadOnlyList<QbPayment> Payments,
    IReadOnlyList<QbAddResult> AddResults);

public sealed class QbxmlParser
{
    /// <summary>
    /// Parse a full QBXMLMsgsRs response (one cycle's response XML from QB).
    /// Tolerates partial / missing fields per qbXML schema variance.
    /// </summary>
    public ParseResult ParseResponse(string xml)
    {
        var invoices = new List<QbInvoice>();
        var payments = new List<QbPayment>();
        var adds = new List<QbAddResult>();

        if (string.IsNullOrWhiteSpace(xml)) return new ParseResult(invoices, payments, adds);

        var doc = XDocument.Parse(xml);

        foreach (var invRs in doc.Descendants("InvoiceQueryRs"))
            foreach (var inv in invRs.Elements("InvoiceRet"))
                invoices.Add(ParseInvoice(inv));

        foreach (var payRs in doc.Descendants("ReceivePaymentQueryRs"))
            foreach (var pay in payRs.Elements("ReceivePaymentRet"))
                payments.Add(ParsePayment(pay));

        // Write-back response parsing. Each Add response carries statusCode
        // (0 = success), statusMessage, and (on success) the new ListID.
        foreach (var addRs in doc.Descendants("CustomerAddRs"))
            adds.Add(ParseAddResult(addRs, "CustomerAdd", "CustomerRet"));

        foreach (var addRs in doc.Descendants("VendorAddRs"))
            adds.Add(ParseAddResult(addRs, "VendorAdd", "VendorRet"));

        return new ParseResult(invoices, payments, adds);
    }

    private static QbAddResult ParseAddResult(XElement rs, string verb, string retElementName)
    {
        var requestIdStr = rs.Attribute("requestID")?.Value;
        int? requestId = int.TryParse(requestIdStr, out var rid) ? rid : null;

        var statusCode = rs.Attribute("statusCode")?.Value ?? "0";
        var statusMessage = rs.Attribute("statusMessage")?.Value;

        if (statusCode == "0")
        {
            var ret = rs.Element(retElementName);
            var listId = ret?.Element("ListID")?.Value;
            var fullName = ret?.Element("FullName")?.Value ?? ret?.Element("Name")?.Value;
            return new QbAddResult(requestId, verb, true, listId, fullName, null);
        }

        return new QbAddResult(requestId, verb, false, null, null, statusMessage ?? $"statusCode={statusCode}");
    }

    private static QbInvoice ParseInvoice(XElement inv)
    {
        var ref_ = inv.Element("RefNumber")?.Value ?? "";
        var txnId = inv.Element("TxnID")?.Value;
        var txnDate = ParseDate(inv.Element("TxnDate")?.Value);
        var modifiedAt = ParseDateTime(inv.Element("TimeModified")?.Value);
        var customer = inv.Element("CustomerRef")?.Element("FullName")?.Value;
        var subtotal = ParseDec(inv.Element("Subtotal")?.Value);
        var balance = ParseDec(inv.Element("BalanceRemaining")?.Value);
        var applied = ParseDec(inv.Element("AppliedAmount")?.Value);
        var isPaidRaw = inv.Element("IsPaid")?.Value;
        var isPaid = isPaidRaw is not null && string.Equals(isPaidRaw, "true", StringComparison.OrdinalIgnoreCase);

        return new QbInvoice(ref_, txnId, txnDate, customer, subtotal, balance, applied, isPaid, modifiedAt);
    }

    private static QbPayment ParsePayment(XElement pay)
    {
        var txnId = pay.Element("TxnID")?.Value;
        var txnDate = ParseDate(pay.Element("TxnDate")?.Value);
        var customer = pay.Element("CustomerRef")?.Element("FullName")?.Value;
        var total = ParseDec(pay.Element("TotalAmount")?.Value);
        var unused = ParseDec(pay.Element("UnusedPayment")?.Value);

        var applied = new List<QbPaymentAppliedToInvoice>();
        foreach (var line in pay.Elements("AppliedToTxnRet"))
        {
            var invRef = line.Element("RefNumber")?.Value ?? "";
            var invTxnId = line.Element("TxnID")?.Value;
            var amt = ParseDec(line.Element("Amount")?.Value) ?? 0m;
            var bal = ParseDec(line.Element("BalanceRemaining")?.Value);
            applied.Add(new QbPaymentAppliedToInvoice(invRef, invTxnId, amt, bal));
        }

        return new QbPayment(txnId, txnDate, customer, total, unused, applied);
    }

    private static DateTime? ParseDate(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? d : null;

    private static DateTime? ParseDateTime(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : null;

    private static decimal? ParseDec(string? s) =>
        decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;
}
