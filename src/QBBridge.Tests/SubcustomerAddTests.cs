using System.Xml.Linq;
using QBBridge.Service.Soap;
using QBBridge.Service.Workflow;

namespace QBBridge.Tests;

/// <summary>
/// Tests for Phase 3 write-back: SubcustomerAdd qbXML build (CustomerAdd
/// with ParentRef). The response shape is identical to a regular Customer
/// AddRs, so we lean on the existing CustomerAddRs parser tests for that
/// half — these focus on the parent-reference and JobDesc structure.
/// </summary>
public sealed class SubcustomerAddTests
{
    private readonly QbxmlBuilder _b = new();

    private static PendingClaim SampleClaim(int id = 166524) => new(
        QbClaimsId: id,
        ClaimId: 242703,
        ClaimName: "Joseph Hammerslough 242703",
        ParentCustomerName: "Paradigm Health Corp 1537",
        ClaimNumber: "8300153807",
        LastChangeDate: new DateTime(2026, 4, 16, 23, 33, 28, DateTimeKind.Utc));

    [Fact]
    public void SubcustomerAdd_ProducesValidXml()
    {
        var xml = _b.SubcustomerAdd(SampleClaim(), 3000);
        var doc = XDocument.Parse(xml);
        Assert.NotNull(doc.Descendants("CustomerAddRq").FirstOrDefault());
        Assert.NotNull(doc.Descendants("CustomerAdd").FirstOrDefault());
    }

    [Fact]
    public void SubcustomerAdd_HasRequestIdInPhase3Range()
    {
        var xml = _b.SubcustomerAdd(SampleClaim(), 3042);
        var doc = XDocument.Parse(xml);
        Assert.Equal("3042", doc.Descendants("CustomerAddRq").First().Attribute("requestID")?.Value);
    }

    [Fact]
    public void SubcustomerAdd_SetsParentRefFullName()
    {
        var xml = _b.SubcustomerAdd(SampleClaim(), 3000);
        var doc = XDocument.Parse(xml);
        var parentRef = doc.Descendants("ParentRef").FirstOrDefault();
        Assert.NotNull(parentRef);
        Assert.Equal("Paradigm Health Corp 1537", parentRef!.Element("FullName")?.Value);
    }

    [Fact]
    public void SubcustomerAdd_NameIsClaimIntfId()
    {
        var xml = _b.SubcustomerAdd(SampleClaim(), 3000);
        var doc = XDocument.Parse(xml);
        Assert.Equal("Joseph Hammerslough 242703", doc.Descendants("Name").First().Value);
    }

    [Fact]
    public void SubcustomerAdd_ClaimNumberGoesToJobDesc()
    {
        var xml = _b.SubcustomerAdd(SampleClaim(), 3000);
        var doc = XDocument.Parse(xml);
        Assert.Equal("8300153807", doc.Descendants("JobDesc").First().Value);
    }

    [Fact]
    public void SubcustomerAdd_OmitsJobDescWhenClaimNumberNull()
    {
        var c = SampleClaim() with { ClaimNumber = null };
        var xml = _b.SubcustomerAdd(c, 3000);
        var doc = XDocument.Parse(xml);
        Assert.Empty(doc.Descendants("JobDesc"));
    }

    [Fact]
    public void SubcustomerAdd_ThrowsWhenClaimNameMissing()
    {
        var c = SampleClaim() with { ClaimName = null };
        Assert.Throws<InvalidOperationException>(() => _b.SubcustomerAdd(c, 3000));
    }

    [Fact]
    public void SubcustomerAdd_ThrowsWhenParentCustomerNameMissing()
    {
        // Backend SHOULD never send a claim with null parent (INNER JOIN
        // guarantees it), but defend against bad input anyway.
        var c = SampleClaim() with { ParentCustomerName = null };
        Assert.Throws<InvalidOperationException>(() => _b.SubcustomerAdd(c, 3000));
    }

    [Fact]
    public void SubcustomerAdd_TruncatesNameTo41Chars()
    {
        var c = SampleClaim() with { ClaimName = new string('X', 200) };
        var xml = _b.SubcustomerAdd(c, 3000);
        var doc = XDocument.Parse(xml);
        Assert.Equal(41, doc.Descendants("Name").First().Value.Length);
    }

    [Fact]
    public void SubcustomerAdd_DoesNotTruncateParentName()
    {
        // Parent name must match QB exactly. Truncation would mismatch and
        // cause QB to fail the Add with "ParentRef not found." Better to let
        // QB reject the long name explicitly so we can investigate the cause
        // (which would itself indicate that our Customer truncation in Phase 1
        // produced a name that QB also can't store — symptomatic of a bug).
        var c = SampleClaim() with { ParentCustomerName = "Some Very Long Customer Name That Is Way Over 41 Chars 12345" };
        var xml = _b.SubcustomerAdd(c, 3000);
        var doc = XDocument.Parse(xml);
        Assert.Equal(
            "Some Very Long Customer Name That Is Way Over 41 Chars 12345",
            doc.Descendants("ParentRef").First().Element("FullName")?.Value);
    }

    [Fact]
    public void SubcustomerAdd_EscapesXmlSpecialCharsInParent()
    {
        // Defensive: parent names with & could break XML if not escaped.
        var c = SampleClaim() with { ParentCustomerName = "Smith & Jones <LLC> 9999" };
        var xml = _b.SubcustomerAdd(c, 3000);
        var doc = XDocument.Parse(xml);
        Assert.Equal("Smith & Jones <LLC> 9999",
            doc.Descendants("ParentRef").First().Element("FullName")?.Value);
    }

    [Fact]
    public void SubcustomerAdd_HasStopOnError()
    {
        var xml = _b.SubcustomerAdd(SampleClaim(), 3000);
        Assert.Contains("onError=\"stopOnError\"", xml);
    }

    [Fact]
    public void SubcustomerAdd_StructureMinimal()
    {
        // Sub-customers are intentionally minimal: Name + ParentRef + JobDesc.
        // No address/phone/contact fields — those live on the parent.
        var xml = _b.SubcustomerAdd(SampleClaim(), 3000);
        var doc = XDocument.Parse(xml);
        Assert.Empty(doc.Descendants("BillAddress"));
        Assert.Empty(doc.Descendants("Phone"));
        Assert.Empty(doc.Descendants("CompanyName"));
        Assert.Empty(doc.Descendants("TermsRef"));
    }
}
