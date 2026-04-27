using System.Globalization;

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
}
