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
    /// BillQuery — contractor-pay side mirror of InvoiceQuery. Pulls bill
    /// number, date, vendor, AmountDue, OpenAmount (what's still unpaid),
    /// IsPaid flag.
    /// </summary>
    public string BillQuery(DateTime sinceUtc)
    {
        return Wrap($"""
            <BillQueryRq requestID="3">
              <ModifiedDateRangeFilter>
                <FromModifiedDate>{Iso(sinceUtc)}</FromModifiedDate>
              </ModifiedDateRangeFilter>
              <IncludeLineItems>false</IncludeLineItems>
              <IncludeLinkedTxns>false</IncludeLinkedTxns>
              <OwnerID>0</OwnerID>
            </BillQueryRq>
            """);
    }

    /// <summary>
    /// BillPaymentCheckQuery since given timestamp. JNJ pays contractors
    /// predominantly by check; this is the primary bill-payment source.
    /// </summary>
    public string BillPaymentCheckQuery(DateTime sinceUtc)
    {
        return Wrap($"""
            <BillPaymentCheckQueryRq requestID="4">
              <ModifiedDateRangeFilter>
                <FromModifiedDate>{Iso(sinceUtc)}</FromModifiedDate>
              </ModifiedDateRangeFilter>
              <IncludeLineItems>true</IncludeLineItems>
            </BillPaymentCheckQueryRq>
            """);
    }

    /// <summary>
    /// BillPaymentCreditCardQuery since given timestamp. Covers the case where
    /// JNJ has paid a contractor via QB credit-card transaction. Same shape as
    /// BillPaymentCheck for our purposes; downstream we tag PaymentMethod for
    /// audit-trail visibility but otherwise treat them identically.
    /// </summary>
    public string BillPaymentCreditCardQuery(DateTime sinceUtc)
    {
        return Wrap($"""
            <BillPaymentCreditCardQueryRq requestID="5">
              <ModifiedDateRangeFilter>
                <FromModifiedDate>{Iso(sinceUtc)}</FromModifiedDate>
              </ModifiedDateRangeFilter>
              <IncludeLineItems>true</IncludeLineItems>
            </BillPaymentCreditCardQueryRq>
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

    /// <summary>
    /// VendorAdd qbXML for a QB Vendor record (Phase 2: QBContractors → QB Vendor).
    ///
    /// Address rules: QB Vendors have a single VendorAddress block. QBContractors
    /// stores a mailing address (ml*) and a physical address separately, so we
    /// prefer mailing when Address1 is populated, otherwise fall back to physical.
    /// The Addressflag column suggests which is canonical — historic data shows
    /// 'A' when only the physical address is set; we treat null/empty mailing as
    /// the trigger to fall back regardless of the flag.
    ///
    /// 1099 handling: flag1099 column is int (1=track, 0=skip). Mapped to
    /// IsVendorEligibleFor1099 boolean. TaxID populates VendorTaxIdent.
    ///
    /// Phone: WorkPhone → Phone, CellPhone → AltPhone.
    /// </summary>
    public string VendorAdd(PendingContractor v, int requestId)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<VendorAddRq requestID=\"{requestId}\">");
        sb.AppendLine("  <VendorAdd>");

        var name = Truncate(v.FullName, 41);
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException(
                $"QBContractors.ContractorIntfID is null/empty for QBContractorsID={v.QbContractorsId}; cannot build VendorAdd");
        sb.AppendLine($"    <Name>{X(name)}</Name>");

        if (!string.IsNullOrWhiteSpace(v.CompanyName))
            sb.AppendLine($"    <CompanyName>{X(Truncate(v.CompanyName, 41))}</CompanyName>");
        if (!string.IsNullOrWhiteSpace(v.FirstName))
            sb.AppendLine($"    <FirstName>{X(Truncate(v.FirstName, 25))}</FirstName>");
        if (!string.IsNullOrWhiteSpace(v.MiddleName))
            sb.AppendLine($"    <MiddleName>{X(Truncate(v.MiddleName, 5))}</MiddleName>");
        if (!string.IsNullOrWhiteSpace(v.LastName))
            sb.AppendLine($"    <LastName>{X(Truncate(v.LastName, 25))}</LastName>");

        // Address selection: prefer mailing, fall back to physical.
        var addr1 = !string.IsNullOrWhiteSpace(v.MailAddress1) ? v.MailAddress1 : v.Address1;
        var addr2 = !string.IsNullOrWhiteSpace(v.MailAddress1) ? v.MailAddress2 : v.Address2;
        var addr3 = !string.IsNullOrWhiteSpace(v.MailAddress1) ? v.MailAddress3 : null; // physical has no Address3
        var city  = !string.IsNullOrWhiteSpace(v.MailAddress1) ? v.MailCity      : v.City;
        var state = !string.IsNullOrWhiteSpace(v.MailAddress1) ? v.MailStateCode : v.StateCode;
        var zip   = !string.IsNullOrWhiteSpace(v.MailAddress1) ? v.MailZipCode   : v.ZipCode;

        if (!string.IsNullOrWhiteSpace(addr1))
        {
            sb.AppendLine("    <VendorAddress>");
            sb.AppendLine($"      <Addr1>{X(Truncate(addr1, 41))}</Addr1>");
            if (!string.IsNullOrWhiteSpace(addr2)) sb.AppendLine($"      <Addr2>{X(Truncate(addr2, 41))}</Addr2>");
            if (!string.IsNullOrWhiteSpace(addr3)) sb.AppendLine($"      <Addr3>{X(Truncate(addr3, 41))}</Addr3>");
            if (!string.IsNullOrWhiteSpace(city))  sb.AppendLine($"      <City>{X(Truncate(city, 31))}</City>");
            if (!string.IsNullOrWhiteSpace(state)) sb.AppendLine($"      <State>{X(Truncate(state, 21))}</State>");
            if (!string.IsNullOrWhiteSpace(zip))   sb.AppendLine($"      <PostalCode>{X(Truncate(zip, 13))}</PostalCode>");
            sb.AppendLine("    </VendorAddress>");
        }

        if (!string.IsNullOrWhiteSpace(v.WorkPhone))
            sb.AppendLine($"    <Phone>{X(Truncate(v.WorkPhone, 21))}</Phone>");
        if (!string.IsNullOrWhiteSpace(v.CellPhone))
            sb.AppendLine($"    <AltPhone>{X(Truncate(v.CellPhone, 21))}</AltPhone>");
        if (!string.IsNullOrWhiteSpace(v.Fax))
            sb.AppendLine($"    <Fax>{X(Truncate(v.Fax, 21))}</Fax>");

        // 1099 fields. flag1099 is non-null int in DB but treat null defensively.
        if (!string.IsNullOrWhiteSpace(v.TaxId))
            sb.AppendLine($"    <VendorTaxIdent>{X(Truncate(v.TaxId, 15))}</VendorTaxIdent>");
        if (v.Flag1099.HasValue)
            sb.AppendLine($"    <IsVendorEligibleFor1099>{(v.Flag1099 == 1 ? "true" : "false")}</IsVendorEligibleFor1099>");

        // List refs. Failure to resolve in QB → AddRs returns error, ImportFlag stays 0.
        if (!string.IsNullOrWhiteSpace(v.TermsCode))
            sb.AppendLine($"    <TermsRef><FullName>{X(v.TermsCode)}</FullName></TermsRef>");
        if (!string.IsNullOrWhiteSpace(v.ContractorTypeCode))
            sb.AppendLine($"    <VendorTypeRef><FullName>{X(v.ContractorTypeCode)}</FullName></VendorTypeRef>");

        sb.AppendLine("  </VendorAdd>");
        sb.AppendLine("</VendorAddRq>");

        return Wrap(sb.ToString().TrimEnd());
    }

    /// <summary>
    /// CustomerAdd qbXML for a QB sub-customer / Job (Phase 3: QBClaims → QB
    /// Customer-with-ParentRef). QB treats sub-customers and Jobs identically;
    /// they're just Customer records whose ParentRef points at another Customer.
    ///
    /// The parent customer must exist in QB before this Add succeeds. If the
    /// parent name doesn't resolve in QB, CustomerAddRs returns statusCode 3120
    /// "Object … of … specified in the request cannot be found", we capture the
    /// error message, leave ImportFlag=0 on the InTime row, and the claim
    /// retries next cycle (presumably after the parent has been added).
    ///
    /// requestID range: 3000-3999 for claim Adds (separate from 1000-1999
    /// customer Adds and 2000-2999 vendor Adds). When customers and claims
    /// are both queued in the same cycle, customers run first because their
    /// requests sit at the front of session.PendingRequests.
    /// </summary>
    public string SubcustomerAdd(PendingClaim claim, int requestId)
    {
        var name = Truncate(claim.ClaimName, 41);
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException(
                $"QBClaims.ClaimIntfID is null/empty for QBClaimsID={claim.QbClaimsId}; cannot build SubcustomerAdd");
        if (string.IsNullOrWhiteSpace(claim.ParentCustomerName))
            throw new InvalidOperationException(
                $"PendingClaim.ParentCustomerName is null/empty for QBClaimsID={claim.QbClaimsId}; backend join did not resolve a parent");

        var sb = new StringBuilder();
        sb.AppendLine($"<CustomerAddRq requestID=\"{requestId}\">");
        sb.AppendLine("  <CustomerAdd>");
        sb.AppendLine($"    <Name>{X(name)}</Name>");
        sb.AppendLine("    <ParentRef>");
        sb.AppendLine($"      <FullName>{X(claim.ParentCustomerName)}</FullName>");
        sb.AppendLine("    </ParentRef>");

        // ClaimNumber goes into JobDesc — useful in QB for searching by claim
        // and matches what Janet's IIF imports have been populating historically.
        if (!string.IsNullOrWhiteSpace(claim.ClaimNumber))
            sb.AppendLine($"    <JobDesc>{X(Truncate(claim.ClaimNumber, 99))}</JobDesc>");

        sb.AppendLine("  </CustomerAdd>");
        sb.AppendLine("</CustomerAddRq>");

        return Wrap(sb.ToString().TrimEnd());
    }

    private static string X(string? s) => SecurityElement.Escape(s ?? "") ?? "";

    private static string? Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s!.Length <= max ? s : s.Substring(0, max));
}
