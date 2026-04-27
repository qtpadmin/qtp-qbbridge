using System.Xml.Linq;
using QBBridge.Service.Soap;

namespace QBBridge.Tests;

public sealed class QbxmlBuilderTests
{
    private readonly QbxmlBuilder _b = new();

    [Fact]
    public void InvoiceQuery_ProducesValidXml()
    {
        var xml = _b.InvoiceQuery(new DateTime(2025, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        // Should parse without throwing
        var doc = XDocument.Parse(xml);
        Assert.NotNull(doc.Descendants("InvoiceQueryRq").FirstOrDefault());
    }

    [Fact]
    public void InvoiceQuery_IncludesModifiedDateFilter()
    {
        var since = new DateTime(2025, 10, 1, 8, 30, 0, DateTimeKind.Utc);
        var xml = _b.InvoiceQuery(since);
        var doc = XDocument.Parse(xml);

        var filter = doc.Descendants("FromModifiedDate").FirstOrDefault();
        Assert.NotNull(filter);
        Assert.Contains("2025-10-01", filter!.Value);
    }

    [Fact]
    public void InvoiceQuery_HasQbxmlVersionProcessingInstruction()
    {
        var xml = _b.InvoiceQuery(DateTime.UtcNow);
        Assert.Contains("<?qbxml version=\"13.0\"?>", xml);
    }

    [Fact]
    public void InvoiceQuery_HasStopOnErrorAttribute()
    {
        // onError="stopOnError" is important — we want QB to halt a batch
        // rather than silently skip failed records.
        var xml = _b.InvoiceQuery(DateTime.UtcNow);
        Assert.Contains("onError=\"stopOnError\"", xml);
    }

    [Fact]
    public void InvoiceQuery_ExcludesLineItems_ToKeepResponseSmall()
    {
        var xml = _b.InvoiceQuery(DateTime.UtcNow);
        var doc = XDocument.Parse(xml);
        var el = doc.Descendants("IncludeLineItems").FirstOrDefault();
        Assert.NotNull(el);
        Assert.Equal("false", el!.Value);
    }

    [Fact]
    public void ReceivePaymentQuery_IncludesLineItems_ToGetAppliedInvoices()
    {
        // Opposite of InvoiceQuery — for payments we NEED the applied-to lines
        // to figure out which invoices were paid.
        var xml = _b.ReceivePaymentQuery(DateTime.UtcNow);
        var doc = XDocument.Parse(xml);
        var el = doc.Descendants("IncludeLineItems").FirstOrDefault();
        Assert.NotNull(el);
        Assert.Equal("true", el!.Value);
    }

    [Fact]
    public void ReceivePaymentQuery_IncludesModifiedDateFilter()
    {
        var since = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var xml = _b.ReceivePaymentQuery(since);
        var doc = XDocument.Parse(xml);

        var filter = doc.Descendants("FromModifiedDate").FirstOrDefault();
        Assert.NotNull(filter);
        Assert.Contains("2026-01-15", filter!.Value);
    }

    [Fact]
    public void Builder_DateFormat_IsIsoWithoutTimezone()
    {
        // qbXML expects yyyy-MM-ddTHH:mm:ss format. No Z, no offset.
        var dt = new DateTime(2025, 6, 15, 14, 30, 45, DateTimeKind.Utc);
        var xml = _b.InvoiceQuery(dt);
        Assert.Contains("2025-06-15T14:30:45", xml);
        Assert.DoesNotContain("Z", xml.Split('\n').First(l => l.Contains("FromModifiedDate")));
    }
}
