using System.Xml.Linq;
using QBBridge.Service.Soap;
using QBBridge.Service.Workflow;

namespace QBBridge.Tests;

/// <summary>
/// Tests for Phase 2 write-back: VendorAdd qbXML build + VendorAddRs parse.
/// VendorAddRs parsing is generic via ParseAddResult and shares the
/// CustomerAddRs codepath, so we focus tests here on builder behavior +
/// the address-selection logic that's vendor-specific.
/// </summary>
public sealed class VendorAddTests
{
    private readonly QbxmlBuilder _b = new();
    private readonly QbxmlParser _p = new();

    private static PendingContractor SampleFamilyOne(int id = 5161) => new(
        QbContractorsId: id,
        ContractorId: 10755,
        FullName: "Family One Transportation 10755",
        CompanyName: "Family One Transportation Inc",
        FirstName: null,
        MiddleName: null,
        LastName: null,
        MailingName: null,
        // Mailing address null in this sample — physical fallback should kick in
        MailAddress1: null,
        MailAddress2: null,
        MailAddress3: null,
        MailCity: null,
        MailStateCode: null,
        MailZipCode: null,
        Address1: "550 Darby Creek Rd",
        Address2: null,
        City: "Lexington",
        StateCode: "KY",
        ZipCode: "40509",
        AddressFlag: "A",
        WorkPhone: "(859) 967-6181",
        CellPhone: null,
        Fax: null,
        TaxId: "82-3401120",
        Flag1099: 0,
        ContractorTypeCode: "VEND",
        TermsCode: "Due Upon Receipt",
        LastChangeDate: new DateTime(2026, 4, 7, 12, 41, 19, DateTimeKind.Utc));

    [Fact]
    public void VendorAdd_ProducesValidXml()
    {
        var xml = _b.VendorAdd(SampleFamilyOne(), 2000);
        var doc = XDocument.Parse(xml);
        Assert.NotNull(doc.Descendants("VendorAddRq").FirstOrDefault());
        Assert.NotNull(doc.Descendants("VendorAdd").FirstOrDefault());
    }

    [Fact]
    public void VendorAdd_HasRequestId()
    {
        var xml = _b.VendorAdd(SampleFamilyOne(), 2042);
        var doc = XDocument.Parse(xml);
        var rq = doc.Descendants("VendorAddRq").First();
        Assert.Equal("2042", rq.Attribute("requestID")?.Value);
    }

    [Fact]
    public void VendorAdd_NameComesFromFullName()
    {
        var xml = _b.VendorAdd(SampleFamilyOne(), 2000);
        var doc = XDocument.Parse(xml);
        Assert.Equal("Family One Transportation 10755", doc.Descendants("Name").First().Value);
    }

    [Fact]
    public void VendorAdd_ThrowsWhenFullNameMissing()
    {
        var v = SampleFamilyOne() with { FullName = null };
        Assert.Throws<InvalidOperationException>(() => _b.VendorAdd(v, 2000));
    }

    [Fact]
    public void VendorAdd_PrefersMailingAddress_WhenSet()
    {
        var v = SampleFamilyOne() with
        {
            MailAddress1 = "PO Box 999",
            MailCity = "Atlanta",
            MailStateCode = "GA",
            MailZipCode = "30303",
        };
        var xml = _b.VendorAdd(v, 2000);
        var doc = XDocument.Parse(xml);
        var addr = doc.Descendants("VendorAddress").First();
        Assert.Equal("PO Box 999", addr.Element("Addr1")?.Value);
        Assert.Equal("Atlanta", addr.Element("City")?.Value);
        Assert.Equal("GA", addr.Element("State")?.Value);
        Assert.Equal("30303", addr.Element("PostalCode")?.Value);
    }

    [Fact]
    public void VendorAdd_FallsBackToPhysicalAddress_WhenMailingNull()
    {
        // Sample has null mailing — should use Address1 (physical)
        var xml = _b.VendorAdd(SampleFamilyOne(), 2000);
        var doc = XDocument.Parse(xml);
        var addr = doc.Descendants("VendorAddress").First();
        Assert.Equal("550 Darby Creek Rd", addr.Element("Addr1")?.Value);
        Assert.Equal("Lexington", addr.Element("City")?.Value);
        Assert.Equal("KY", addr.Element("State")?.Value);
        Assert.Equal("40509", addr.Element("PostalCode")?.Value);
    }

    [Fact]
    public void VendorAdd_OmitsAddress_WhenBothMailingAndPhysicalAreNull()
    {
        var v = SampleFamilyOne() with
        {
            MailAddress1 = null,
            Address1 = null,
        };
        var xml = _b.VendorAdd(v, 2000);
        var doc = XDocument.Parse(xml);
        Assert.Empty(doc.Descendants("VendorAddress"));
    }

    [Fact]
    public void VendorAdd_TaxIdAndFlag1099_Rendered()
    {
        var xml = _b.VendorAdd(SampleFamilyOne(), 2000);
        var doc = XDocument.Parse(xml);
        Assert.Equal("82-3401120", doc.Descendants("VendorTaxIdent").First().Value);
        Assert.Equal("false", doc.Descendants("IsVendorEligibleFor1099").First().Value);
    }

    [Fact]
    public void VendorAdd_Flag1099True_RendersTrue()
    {
        var v = SampleFamilyOne() with { Flag1099 = 1 };
        var xml = _b.VendorAdd(v, 2000);
        var doc = XDocument.Parse(xml);
        Assert.Equal("true", doc.Descendants("IsVendorEligibleFor1099").First().Value);
    }

    [Fact]
    public void VendorAdd_PhoneMapping_WorkPhone_AltPhoneFromCell()
    {
        var v = SampleFamilyOne() with
        {
            WorkPhone = "555-1111",
            CellPhone = "555-2222",
        };
        var xml = _b.VendorAdd(v, 2000);
        var doc = XDocument.Parse(xml);
        Assert.Equal("555-1111", doc.Descendants("Phone").First().Value);
        Assert.Equal("555-2222", doc.Descendants("AltPhone").First().Value);
    }

    [Fact]
    public void VendorAdd_TermsAndVendorType_RenderedAsListRefs()
    {
        var xml = _b.VendorAdd(SampleFamilyOne(), 2000);
        var doc = XDocument.Parse(xml);
        Assert.Equal("Due Upon Receipt", doc.Descendants("TermsRef").First().Element("FullName")?.Value);
        Assert.Equal("VEND", doc.Descendants("VendorTypeRef").First().Element("FullName")?.Value);
    }

    [Fact]
    public void VendorAdd_HasStopOnError()
    {
        var xml = _b.VendorAdd(SampleFamilyOne(), 2000);
        Assert.Contains("onError=\"stopOnError\"", xml);
    }

    [Fact]
    public void VendorAdd_OmitsOptionalFieldsWhenNull()
    {
        var v = SampleFamilyOne() with
        {
            CompanyName = null,
            WorkPhone = null,
            CellPhone = null,
            Fax = null,
            TaxId = null,
            Flag1099 = null,
            TermsCode = null,
            ContractorTypeCode = null,
        };
        var xml = _b.VendorAdd(v, 2000);
        var doc = XDocument.Parse(xml);
        Assert.Empty(doc.Descendants("CompanyName"));
        Assert.Empty(doc.Descendants("Phone"));
        Assert.Empty(doc.Descendants("AltPhone"));
        Assert.Empty(doc.Descendants("Fax"));
        Assert.Empty(doc.Descendants("VendorTaxIdent"));
        Assert.Empty(doc.Descendants("IsVendorEligibleFor1099"));
        Assert.Empty(doc.Descendants("TermsRef"));
        Assert.Empty(doc.Descendants("VendorTypeRef"));
    }

    [Fact]
    public void ParseResponse_VendorAddRs_Success_ExtractsListId()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <QBXML>
              <QBXMLMsgsRs>
                <VendorAddRs requestID="2000" statusCode="0" statusSeverity="Info" statusMessage="Status OK">
                  <VendorRet>
                    <ListID>50000003-1714506789</ListID>
                    <FullName>Family One Transportation 10755</FullName>
                    <Name>Family One Transportation 10755</Name>
                  </VendorRet>
                </VendorAddRs>
              </QBXMLMsgsRs>
            </QBXML>
            """;
        var r = _p.ParseResponse(xml);
        Assert.Single(r.AddResults);
        var add = r.AddResults[0];
        Assert.Equal(2000, add.RequestId);
        Assert.Equal("VendorAdd", add.Verb);
        Assert.True(add.Ok);
        Assert.Equal("50000003-1714506789", add.ListId);
        Assert.Equal("Family One Transportation 10755", add.FullName);
    }

    [Fact]
    public void ParseResponse_VendorAddRs_DuplicateNameError_CapturesMessage()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <QBXML>
              <QBXMLMsgsRs>
                <VendorAddRs requestID="2001" statusCode="3100" statusSeverity="Error"
                  statusMessage="The name &quot;Family One Transportation 10755&quot; of the list element is already in use." />
              </QBXMLMsgsRs>
            </QBXML>
            """;
        var r = _p.ParseResponse(xml);
        Assert.Single(r.AddResults);
        Assert.False(r.AddResults[0].Ok);
        Assert.Contains("already in use", r.AddResults[0].Error);
        Assert.Equal(2001, r.AddResults[0].RequestId);
    }

    [Fact]
    public void ParseResponse_MixedCustomerAndVendorAdds_AllSeparated()
    {
        // A single QBWC cycle can return both CustomerAdd AND VendorAdd results.
        // requestID ranges (1000+ vs 2000+) keep them disambiguated for ack lookup.
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <QBXML>
              <QBXMLMsgsRs>
                <CustomerAddRs requestID="1000" statusCode="0" statusMessage="OK">
                  <CustomerRet><ListID>C1</ListID><FullName>Cust 1</FullName></CustomerRet>
                </CustomerAddRs>
                <VendorAddRs requestID="2000" statusCode="0" statusMessage="OK">
                  <VendorRet><ListID>V1</ListID><FullName>Vend 1</FullName></VendorRet>
                </VendorAddRs>
              </QBXMLMsgsRs>
            </QBXML>
            """;
        var r = _p.ParseResponse(xml);
        Assert.Equal(2, r.AddResults.Count);
        var cust = r.AddResults.First(a => a.Verb == "CustomerAdd");
        var vend = r.AddResults.First(a => a.Verb == "VendorAdd");
        Assert.Equal(1000, cust.RequestId);
        Assert.Equal(2000, vend.RequestId);
        Assert.Equal("C1", cust.ListId);
        Assert.Equal("V1", vend.ListId);
    }
}
