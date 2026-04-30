using System.Xml.Linq;
using QBBridge.Service.Soap;
using QBBridge.Service.Workflow;

namespace QBBridge.Tests;

/// <summary>
/// Tests for Phase 1 write-back: CustomerAdd qbXML build + CustomerAddRs parse.
/// These cover both happy-path and error-path responses from QuickBooks.
/// </summary>
public sealed class CustomerAddTests
{
    private readonly QbxmlBuilder _b = new();
    private readonly QbxmlParser _p = new();

    private static PendingCustomer SampleDavies(int id = 813) => new(
        QbCustomersId: id,
        CustomerId: 5406,
        FullName: "Davies Claims Solutions 5406",
        CompanyName: "Davies Claims Solutions",
        FirstName: null,
        LastName: null,
        MailingName: null,
        Address1: "PO Box 291587",
        Address2: null,
        Address3: null,
        City: "Nashville",
        StateCode: "TN",
        ZipCode: "37229",
        Phone: null,
        TollfreePhone: null,
        Fax: null,
        CustomerTypeCode: "INSWC",
        TermsCode: "NET 30",
        LastChangeDate: new DateTime(2026, 4, 16, 23, 39, 19, DateTimeKind.Utc));

    [Fact]
    public void CustomerAdd_ProducesValidXml()
    {
        var xml = _b.CustomerAdd(SampleDavies(), 1000);
        var doc = XDocument.Parse(xml);
        Assert.NotNull(doc.Descendants("CustomerAddRq").FirstOrDefault());
        Assert.NotNull(doc.Descendants("CustomerAdd").FirstOrDefault());
    }

    [Fact]
    public void CustomerAdd_HasRequestId_ForCorrelation()
    {
        var xml = _b.CustomerAdd(SampleDavies(), 1042);
        var doc = XDocument.Parse(xml);
        var rq = doc.Descendants("CustomerAddRq").First();
        Assert.Equal("1042", rq.Attribute("requestID")?.Value);
    }

    [Fact]
    public void CustomerAdd_NameComesFromFullName()
    {
        var xml = _b.CustomerAdd(SampleDavies(), 1000);
        var doc = XDocument.Parse(xml);
        Assert.Equal("Davies Claims Solutions 5406", doc.Descendants("Name").First().Value);
    }

    [Fact]
    public void CustomerAdd_TruncatesNameTo41Chars()
    {
        var c = SampleDavies() with { FullName = new string('X', 100) };
        var xml = _b.CustomerAdd(c, 1000);
        var doc = XDocument.Parse(xml);
        Assert.Equal(41, doc.Descendants("Name").First().Value.Length);
    }

    [Fact]
    public void CustomerAdd_ThrowsWhenFullNameMissing()
    {
        var c = SampleDavies() with { FullName = null };
        Assert.Throws<InvalidOperationException>(() => _b.CustomerAdd(c, 1000));
    }

    [Fact]
    public void CustomerAdd_OmitsBillAddressWhenAddress1Null()
    {
        var c = SampleDavies() with { Address1 = null };
        var xml = _b.CustomerAdd(c, 1000);
        var doc = XDocument.Parse(xml);
        Assert.Empty(doc.Descendants("BillAddress"));
    }

    [Fact]
    public void CustomerAdd_IncludesBillAddressWhenAddress1Set()
    {
        var xml = _b.CustomerAdd(SampleDavies(), 1000);
        var doc = XDocument.Parse(xml);
        var addr = doc.Descendants("BillAddress").FirstOrDefault();
        Assert.NotNull(addr);
        Assert.Equal("PO Box 291587", addr!.Element("Addr1")?.Value);
        Assert.Equal("Nashville", addr.Element("City")?.Value);
        Assert.Equal("TN", addr.Element("State")?.Value);
        Assert.Equal("37229", addr.Element("PostalCode")?.Value);
    }

    [Fact]
    public void CustomerAdd_EscapesXmlSpecialChars()
    {
        // Defensive: customer names with & < > " ' could break XML if not escaped.
        var c = SampleDavies() with
        {
            FullName = "Smith & Jones <Law>",
            CompanyName = "Smith & Jones <Law> \"LLC\""
        };
        var xml = _b.CustomerAdd(c, 1000);
        // Should round-trip through XDocument.Parse without throwing
        var doc = XDocument.Parse(xml);
        Assert.Equal("Smith & Jones <Law>", doc.Descendants("Name").First().Value);
        Assert.Equal("Smith & Jones <Law> \"LLC\"", doc.Descendants("CompanyName").First().Value);
    }

    [Fact]
    public void CustomerAdd_TermsAndTypeRendered_AsListRefs()
    {
        var xml = _b.CustomerAdd(SampleDavies(), 1000);
        var doc = XDocument.Parse(xml);
        Assert.Equal("NET 30", doc.Descendants("TermsRef").First().Element("FullName")?.Value);
        Assert.Equal("INSWC", doc.Descendants("CustomerTypeRef").First().Element("FullName")?.Value);
    }

    [Fact]
    public void CustomerAdd_HasStopOnError_ForBatchHalt()
    {
        // Same convention as the read queries — if one Add fails, halt the batch
        // rather than silently skip subsequent ones.
        var xml = _b.CustomerAdd(SampleDavies(), 1000);
        Assert.Contains("onError=\"stopOnError\"", xml);
    }

    [Fact]
    public void CustomerAdd_OmitsOptionalFieldsWhenNull()
    {
        var c = SampleDavies() with
        {
            CompanyName = null,
            Phone = null,
            TollfreePhone = null,
            Fax = null,
            TermsCode = null,
            CustomerTypeCode = null,
        };
        var xml = _b.CustomerAdd(c, 1000);
        var doc = XDocument.Parse(xml);
        Assert.Empty(doc.Descendants("CompanyName"));
        Assert.Empty(doc.Descendants("Phone"));
        Assert.Empty(doc.Descendants("AltPhone"));
        Assert.Empty(doc.Descendants("Fax"));
        Assert.Empty(doc.Descendants("TermsRef"));
        Assert.Empty(doc.Descendants("CustomerTypeRef"));
    }

    // ─── Parser tests ────────────────────────────────────────────────────

    [Fact]
    public void ParseResponse_CustomerAddRs_Success_ExtractsListIdAndName()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <QBXML>
              <QBXMLMsgsRs>
                <CustomerAddRs requestID="1000" statusCode="0" statusSeverity="Info" statusMessage="Status OK">
                  <CustomerRet>
                    <ListID>80000027-1714506789</ListID>
                    <FullName>Davies Claims Solutions 5406</FullName>
                    <Name>Davies Claims Solutions 5406</Name>
                  </CustomerRet>
                </CustomerAddRs>
              </QBXMLMsgsRs>
            </QBXML>
            """;
        var r = _p.ParseResponse(xml);
        Assert.Single(r.AddResults);
        var add = r.AddResults[0];
        Assert.Equal(1000, add.RequestId);
        Assert.Equal("CustomerAdd", add.Verb);
        Assert.True(add.Ok);
        Assert.Equal("80000027-1714506789", add.ListId);
        Assert.Equal("Davies Claims Solutions 5406", add.FullName);
        Assert.Null(add.Error);
    }

    [Fact]
    public void ParseResponse_CustomerAddRs_DuplicateNameError_CapturesMessage()
    {
        // QB returns 3100 ("The name … is already in use") when adding a customer
        // whose name collides. We need to capture the message and ack as qb_error.
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <QBXML>
              <QBXMLMsgsRs>
                <CustomerAddRs requestID="1001" statusCode="3100" statusSeverity="Error"
                  statusMessage="The name &quot;Davies Claims Solutions 5406&quot; of the list element is already in use." />
              </QBXMLMsgsRs>
            </QBXML>
            """;
        var r = _p.ParseResponse(xml);
        Assert.Single(r.AddResults);
        var add = r.AddResults[0];
        Assert.Equal(1001, add.RequestId);
        Assert.False(add.Ok);
        Assert.Null(add.ListId);
        Assert.NotNull(add.Error);
        Assert.Contains("already in use", add.Error);
    }

    [Fact]
    public void ParseResponse_MultipleCustomerAddRs_AllParsed()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <QBXML>
              <QBXMLMsgsRs>
                <CustomerAddRs requestID="1000" statusCode="0" statusSeverity="Info" statusMessage="OK">
                  <CustomerRet><ListID>L1</ListID><FullName>Cust A</FullName></CustomerRet>
                </CustomerAddRs>
                <CustomerAddRs requestID="1001" statusCode="3100" statusSeverity="Error" statusMessage="dup" />
                <CustomerAddRs requestID="1002" statusCode="0" statusSeverity="Info" statusMessage="OK">
                  <CustomerRet><ListID>L3</ListID><FullName>Cust C</FullName></CustomerRet>
                </CustomerAddRs>
              </QBXMLMsgsRs>
            </QBXML>
            """;
        var r = _p.ParseResponse(xml);
        Assert.Equal(3, r.AddResults.Count);
        Assert.True(r.AddResults[0].Ok);
        Assert.False(r.AddResults[1].Ok);
        Assert.True(r.AddResults[2].Ok);
        Assert.Equal(1000, r.AddResults[0].RequestId);
        Assert.Equal(1001, r.AddResults[1].RequestId);
        Assert.Equal(1002, r.AddResults[2].RequestId);
    }

    [Fact]
    public void ParseResponse_MixedReadAndWrite_BothExtracted()
    {
        // A single QBWC cycle can return both Invoice query results AND CustomerAdd
        // results in one response. Make sure the parser handles the mix.
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <QBXML>
              <QBXMLMsgsRs>
                <InvoiceQueryRs requestID="1" statusCode="0">
                  <InvoiceRet>
                    <RefNumber>INV-100</RefNumber>
                    <TxnID>T1</TxnID>
                    <CustomerRef><FullName>X</FullName></CustomerRef>
                    <IsPaid>false</IsPaid>
                  </InvoiceRet>
                </InvoiceQueryRs>
                <CustomerAddRs requestID="1000" statusCode="0" statusMessage="OK">
                  <CustomerRet><ListID>LZ</ListID><FullName>Cust Z</FullName></CustomerRet>
                </CustomerAddRs>
              </QBXMLMsgsRs>
            </QBXML>
            """;
        var r = _p.ParseResponse(xml);
        Assert.Single(r.Invoices);
        Assert.Single(r.AddResults);
        Assert.Equal("INV-100", r.Invoices[0].RefNumber);
        Assert.Equal(1000, r.AddResults[0].RequestId);
    }
}
