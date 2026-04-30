using System.Globalization;
using System.Security;
using System.Text;
using QBBridge.Service.Workflow;

namespace QBBridge.Service.Soap;

/// <summary>
/// Builds qbXML request strings.
///
/// Reference: QuickBooks SDK — qbxmlops130.xml (ships with QB SDK, documents all verbs).
/// We target qbXML version 13.0 since that's what QB Enterprise 2024 accepts natively.
/// </summary>
public sealed class QbxmlBuilder
{
    private const string QbxmlVersion = "13.0";

    private static string Wrap(string inner) =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <?qbxml version="{QbxmlVersion}"?>
        <QBXML>
          <QBXMLMsgsRq onError="stopOnError">
            {inner}
          </QBXMLMsgsRq>
        </QBXML>
        """;

    private static string Iso(DateTime d) => d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// InvoiceQuery with ModifiedDateRangeFilter since given timestamp.
    /// Pulls: invoice number, date, customer, amount, applied payment amount,
    /// balance remaining, void/paid flags.
    /// </summary>
    public string InvoiceQuery(DateTime sinceUtc)
    {
        return Wrap($"""
            <InvoiceQueryRq requestID="1">
              <ModifiedDateRangeFilter>
                <FromModifiedDate>{Iso(sinceUtc)}</FromModifiedDate>
              </ModifiedDateRangeFilter>
              <IncludeLineItems>false</IncludeLineItems>
              <IncludeLinkedTxns>false</IncludeLinkedTxns>
              <OwnerID>0</OwnerID>
            </InvoiceQueryRq>
            """);
    }

    /// <summary>
    /// ReceivePaymentQuery since given timestamp. Returns each payment posted
    /// against an invoice — what we need to populate QBPaidDate + QBPaidAmount.
    /// </summary>
    public string ReceivePaymentQuery(DateTime sinceUtc)
    {
        return Wrap($"""
            <ReceivePaymentQueryRq requestID="2">
              <ModifiedDateRangeFilter>
                <FromModifiedDate>{Iso(sinceUtc)}</FromModifiedDate>
              </ModifiedDateRangeFilter>
              <IncludeLineItems>true</IncludeLineItems>
            </ReceivePaymentQueryRq>
            """);
    }

    /// <summary>
    /// CustomerAdd qbXML for a top-level QB Customer record.
    ///
    /// Maps QBCustomers row → QB CustomerAdd. Mirrors the IIF column layout
    /// Janet's existing IAF imports use, so QB sees the same shape it has
    /// historically accepted.
    ///
    /// Address rules:
    ///  - QB BillAddress accepts up to 3 lines + city/state/postal. We map:
    ///    Line1 = Address1, Line2 = Address2, Line3 = Address3 (all optional).
    ///  - If Address1 is null we omit BillAddress entirely (QB rejects empty addr).
    ///
    /// Phone: only the primary Phone field is sent. TollfreePhone / Fax are
    /// stashed in AltPhone / Fax respectively when present.
    ///
    /// Terms / CustomerType: sent as Refs. If the named record doesn't exist
    /// in QB, the AddRs returns an error and the bridge logs it without
    /// flipping ImportFlag — Tonya can fix the InTime row and it retries.
    /// </summary>
    public string CustomerAdd(PendingCustomer c, int requestId)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<CustomerAddRq requestID=\"{requestId}\">");
        sb.AppendLine("  <CustomerAdd>");

        // Name (required, max 41 chars). Match Janet's existing format from CustomerIntfID.
        var name = Truncate(c.FullName, 41);
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException(
                $"QBCustomers.CustomerIntfID is null/empty for QBCustomersID={c.QbCustomersId}; cannot build CustomerAdd");
        sb.AppendLine($"    <Name>{X(name)}</Name>");

        if (!string.IsNullOrWhiteSpace(c.CompanyName))
            sb.AppendLine($"    <CompanyName>{X(Truncate(c.CompanyName, 41))}</CompanyName>");
        if (!string.IsNullOrWhiteSpace(c.FirstName))
            sb.AppendLine($"    <FirstName>{X(Truncate(c.FirstName, 25))}</FirstName>");
        if (!string.IsNullOrWhiteSpace(c.LastName))
            sb.AppendLine($"    <LastName>{X(Truncate(c.LastName, 25))}</LastName>");

        // BillAddress is optional, but if Address1 is present we send a complete block.
        if (!string.IsNullOrWhiteSpace(c.Address1))
        {
            sb.AppendLine("    <BillAddress>");
            sb.AppendLine($"      <Addr1>{X(Truncate(c.Address1, 41))}</Addr1>");
            if (!string.IsNullOrWhiteSpace(c.Address2)) sb.AppendLine($"      <Addr2>{X(Truncate(c.Address2, 41))}</Addr2>");
            if (!string.IsNullOrWhiteSpace(c.Address3)) sb.AppendLine($"      <Addr3>{X(Truncate(c.Address3, 41))}</Addr3>");
            if (!string.IsNullOrWhiteSpace(c.City))     sb.AppendLine($"      <City>{X(Truncate(c.City, 31))}</City>");
            if (!string.IsNullOrWhiteSpace(c.StateCode))sb.AppendLine($"      <State>{X(Truncate(c.StateCode, 21))}</State>");
            if (!string.IsNullOrWhiteSpace(c.ZipCode))  sb.AppendLine($"      <PostalCode>{X(Truncate(c.ZipCode, 13))}</PostalCode>");
            sb.AppendLine("    </BillAddress>");
        }

        if (!string.IsNullOrWhiteSpace(c.Phone))
            sb.AppendLine($"    <Phone>{X(Truncate(c.Phone, 21))}</Phone>");
        if (!string.IsNullOrWhiteSpace(c.TollfreePhone))
            sb.AppendLine($"    <AltPhone>{X(Truncate(c.TollfreePhone, 21))}</AltPhone>");
        if (!string.IsNullOrWhiteSpace(c.Fax))
            sb.AppendLine($"    <Fax>{X(Truncate(c.Fax, 21))}</Fax>");

        // Refs to QB list entities. If the named entry doesn't exist in QB, AddRs
        // returns an error and we leave ImportFlag=0 so it retries.
        if (!string.IsNullOrWhiteSpace(c.TermsCode))
            sb.AppendLine($"    <TermsRef><FullName>{X(c.TermsCode)}</FullName></TermsRef>");
        if (!string.IsNullOrWhiteSpace(c.CustomerTypeCode))
            sb.AppendLine($"    <CustomerTypeRef><FullName>{X(c.CustomerTypeCode)}</FullName></CustomerTypeRef>");

        sb.AppendLine("  </CustomerAdd>");
        sb.AppendLine("</CustomerAddRq>");

        return Wrap(sb.ToString().TrimEnd());
    }

    private static string X(string? s) => SecurityElement.Escape(s ?? "") ?? "";

    private static string? Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s!.Length <= max ? s : s.Substring(0, max));
}
