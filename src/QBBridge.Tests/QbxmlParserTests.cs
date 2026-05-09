using QBBridge.Service.Soap;

namespace QBBridge.Tests;

public sealed class QbxmlParserTests
{
    private readonly QbxmlParser _parser = new();

    private static string LoadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void ParseResponse_Empty_ReturnsEmptyLists()
    {
        var r = _parser.ParseResponse("");
        Assert.Empty(r.Invoices);
        Assert.Empty(r.Payments);
    }

    [Fact]
    public void ParseResponse_Whitespace_ReturnsEmptyLists()
    {
        var r = _parser.ParseResponse("   \n\t  ");
        Assert.Empty(r.Invoices);
        Assert.Empty(r.Payments);
    }

    [Fact]
    public void ParseResponse_InvalidXml_Throws()
    {
        // Malformed XML is a hard failure — parser should not swallow it; the
        // SOAP service catches and reports to QBWC via negative return code.
        Assert.ThrowsAny<System.Xml.XmlException>(() => _parser.ParseResponse("<not-well-formed"));
    }

    [Fact]
    public void ParseResponse_InvoiceFixture_ExtractsAllRows()
    {
        var r = _parser.ParseResponse(LoadFixture("invoice-query-response.xml"));
        Assert.Equal(3, r.Invoices.Count);
        Assert.Empty(r.Payments);
    }

    [Fact]
    public void ParseResponse_InvoiceFixture_FirstRow_FullyPopulated()
    {
        var r = _parser.ParseResponse(LoadFixture("invoice-query-response.xml"));
        var first = r.Invoices[0];

        Assert.Equal("123266", first.RefNumber);
        Assert.Equal("ABCDEF01-1700000000", first.TxnID);
        Assert.Equal(new DateTime(2025, 10, 1), first.TxnDate!.Value.Date);
        Assert.Equal("AmeriSys", first.CustomerFullName);
        Assert.Equal(135.00m, first.Subtotal);
        Assert.Equal(0m, first.BalanceRemaining);
        Assert.Equal(135m, first.AppliedAmount);
        Assert.True(first.IsPaid);
        Assert.NotNull(first.ModifiedAt);
    }

    [Fact]
    public void ParseResponse_InvoiceFixture_UnpaidRow_HasCorrectBalance()
    {
        var r = _parser.ParseResponse(LoadFixture("invoice-query-response.xml"));
        var unpaid = r.Invoices.Single(i => i.RefNumber == "123267");

        Assert.False(unpaid.IsPaid);
        Assert.Equal(168.75m, unpaid.BalanceRemaining);
        Assert.Equal(0m, unpaid.AppliedAmount);
    }

    [Fact]
    public void ParseResponse_InvoiceFixture_MissingOptionalFields_ToleratedAsNull()
    {
        // Third row in the fixture intentionally omits DueDate, AppliedAmount,
        // BalanceRemaining, EditSequence, TxnNumber, TimeModified.
        var r = _parser.ParseResponse(LoadFixture("invoice-query-response.xml"));
        var partial = r.Invoices.Single(i => i.RefNumber == "123268");

        Assert.Equal("Paradigm Health", partial.CustomerFullName);
        Assert.Equal(450m, partial.Subtotal);
        Assert.Null(partial.AppliedAmount);
        Assert.Null(partial.BalanceRemaining);
        Assert.False(partial.IsPaid);
    }

    [Fact]
    public void ParseResponse_PaymentFixture_ExtractsAllRows()
    {
        var r = _parser.ParseResponse(LoadFixture("receive-payment-query-response.xml"));
        Assert.Equal(2, r.Payments.Count);
        Assert.Empty(r.Invoices);
    }

    [Fact]
    public void ParseResponse_PaymentFixture_AppliedLines_ArePreserved()
    {
        var r = _parser.ParseResponse(LoadFixture("receive-payment-query-response.xml"));
        var full = r.Payments[0];

        Assert.Equal("PAY00001-1700100000", full.TxnID);
        Assert.Equal("AmeriSys", full.CustomerFullName);
        Assert.Equal(303.75m, full.TotalAmount);
        Assert.Equal(2, full.AppliedTo.Count);

        var line1 = full.AppliedTo[0];
        Assert.Equal("123266", line1.InvoiceRefNumber);
        Assert.Equal(135.00m, line1.AmountApplied);
        Assert.Equal(0m, line1.BalanceRemaining);

        var line2 = full.AppliedTo[1];
        Assert.Equal("123267", line2.InvoiceRefNumber);
        Assert.Equal(168.75m, line2.AmountApplied);
    }

    [Fact]
    public void ParseResponse_PaymentFixture_PartialPayment_ReflectsRemainingBalance()
    {
        var r = _parser.ParseResponse(LoadFixture("receive-payment-query-response.xml"));
        var partial = r.Payments.Single(p => p.TxnID == "PAY00002-1700100001");

        Assert.Single(partial.AppliedTo);
        var line = partial.AppliedTo[0];
        Assert.Equal("123300", line.InvoiceRefNumber);
        Assert.Equal(1000m, line.AmountApplied);
        Assert.Equal(500m, line.BalanceRemaining);  // invoice still has 500 outstanding
    }

    [Fact]
    public void ParseResponse_MixedFixture_ReturnsBothTypes()
    {
        var r = _parser.ParseResponse(LoadFixture("mixed-query-response.xml"));
        Assert.Single(r.Invoices);
        Assert.Single(r.Payments);
        Assert.Equal("999001", r.Invoices[0].RefNumber);
        Assert.Equal("999001", r.Payments[0].AppliedTo[0].InvoiceRefNumber);
    }

    [Fact]
    public void ParseResponse_UnknownChildElements_DoesNotThrow()
    {
        // Schema variance insurance: QB may add new child elements in future
        // updates. Parser uses explicit field lookups, should ignore unknowns.
        var xml = @"<?xml version=""1.0""?>
<QBXML><QBXMLMsgsRs><InvoiceQueryRs><InvoiceRet>
  <TxnID>FUTURE-1</TxnID>
  <RefNumber>FUTURE-001</RefNumber>
  <NewFieldFromFutureQb>some value</NewFieldFromFutureQb>
  <Subtotal>99.00</Subtotal>
  <IsPaid>true</IsPaid>
</InvoiceRet></InvoiceQueryRs></QBXMLMsgsRs></QBXML>";

        var r = _parser.ParseResponse(xml);
        Assert.Single(r.Invoices);
        Assert.Equal("FUTURE-001", r.Invoices[0].RefNumber);
        Assert.Equal(99m, r.Invoices[0].Subtotal);
        Assert.True(r.Invoices[0].IsPaid);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    public void ParseResponse_IsPaidBoolean_IsCaseInsensitive(string value, bool expected)
    {
        var xml = $@"<?xml version=""1.0""?>
<QBXML><QBXMLMsgsRs><InvoiceQueryRs><InvoiceRet>
  <RefNumber>TST-{value}</RefNumber>
  <IsPaid>{value}</IsPaid>
</InvoiceRet></InvoiceQueryRs></QBXMLMsgsRs></QBXML>";

        var r = _parser.ParseResponse(xml);
        Assert.Equal(expected, r.Invoices[0].IsPaid);
    }

    // ─── Bills (contractor-pay side, mirror of Invoice tests) ───────────────

    [Fact]
    public void ParseResponse_BillFixture_ExtractsAllRows()
    {
        var r = _parser.ParseResponse(LoadFixture("bill-query-response.xml"));
        Assert.Equal(3, r.Bills.Count);
        Assert.Empty(r.Invoices);
        Assert.Empty(r.Payments);
        Assert.Empty(r.BillPayments);
    }

    [Fact]
    public void ParseResponse_BillFixture_FirstRow_FullyPopulated()
    {
        var r = _parser.ParseResponse(LoadFixture("bill-query-response.xml"));
        var first = r.Bills[0];

        Assert.Equal("1037", first.RefNumber);
        Assert.Equal("BILL0001-1700000000", first.TxnID);
        Assert.Equal(new DateTime(2025, 10, 1), first.TxnDate!.Value.Date);
        Assert.Equal("Acme Transport LLC", first.VendorFullName);
        Assert.Equal(288.00m, first.AmountDue);
        Assert.Equal(0m, first.OpenAmount);
        Assert.True(first.IsPaid);
        Assert.NotNull(first.ModifiedAt);
    }

    [Fact]
    public void ParseResponse_BillFixture_UnpaidRow_HasCorrectOpenAmount()
    {
        var r = _parser.ParseResponse(LoadFixture("bill-query-response.xml"));
        var unpaid = r.Bills.Single(b => b.RefNumber == "1880");

        Assert.False(unpaid.IsPaid);
        Assert.Equal(402m, unpaid.AmountDue);
        Assert.Equal(402m, unpaid.OpenAmount);
    }

    [Fact]
    public void ParseResponse_BillFixture_MissingOptionalFields_ToleratedAsNull()
    {
        var r = _parser.ParseResponse(LoadFixture("bill-query-response.xml"));
        var partial = r.Bills.Single(b => b.RefNumber == "20260124");

        Assert.Equal("Cypress Mobility Group", partial.VendorFullName);
        Assert.Equal(3915m, partial.AmountDue);
        Assert.Null(partial.OpenAmount);
        Assert.False(partial.IsPaid);
    }

    [Fact]
    public void ParseResponse_BillPaymentCheckFixture_ExtractsAllRows()
    {
        var r = _parser.ParseResponse(LoadFixture("bill-payment-check-query-response.xml"));
        Assert.Equal(2, r.BillPayments.Count);
        Assert.Empty(r.Bills);
    }

    [Fact]
    public void ParseResponse_BillPaymentCheckFixture_TagsPaymentMethodAsCheck()
    {
        var r = _parser.ParseResponse(LoadFixture("bill-payment-check-query-response.xml"));
        Assert.All(r.BillPayments, bp => Assert.Equal("check", bp.PaymentMethod));
    }

    [Fact]
    public void ParseResponse_BillPaymentCheckFixture_AppliedLines_ArePreserved()
    {
        var r = _parser.ParseResponse(LoadFixture("bill-payment-check-query-response.xml"));
        var full = r.BillPayments[0];

        Assert.Equal("BPC00001-1700100000", full.TxnID);
        Assert.Equal("Acme Transport LLC", full.VendorFullName);
        Assert.Equal(288.00m, full.Amount);
        Assert.Single(full.AppliedTo);

        var line = full.AppliedTo[0];
        Assert.Equal("1037", line.BillRefNumber);
        Assert.Equal(288.00m, line.AmountApplied);
        Assert.Equal(0m, line.OpenAmountAfter);
    }

    [Fact]
    public void ParseResponse_BillPaymentCheckFixture_PartialPayment_ReflectsRemainingOpenAmount()
    {
        var r = _parser.ParseResponse(LoadFixture("bill-payment-check-query-response.xml"));
        var partial = r.BillPayments.Single(bp => bp.TxnID == "BPC00002-1700100001");

        Assert.Single(partial.AppliedTo);
        var line = partial.AppliedTo[0];
        Assert.Equal("20260124", line.BillRefNumber);
        Assert.Equal(2000m, line.AmountApplied);
        Assert.Equal(1915m, line.OpenAmountAfter);  // bill still has 1915 outstanding
    }

    [Fact]
    public void ParseResponse_BillPaymentCreditCard_TagsPaymentMethodAsCreditcard()
    {
        // Synthetic minimal CC payment — schema mirrors check variant.
        var xml = @"<?xml version=""1.0""?>
<QBXML><QBXMLMsgsRs><BillPaymentCreditCardQueryRs>
  <BillPaymentCreditCardRet>
    <TxnID>BPCC00001-1700200000</TxnID>
    <TxnDate>2025-10-25</TxnDate>
    <PayeeEntityRef><FullName>Pinnacle Drivers Inc</FullName></PayeeEntityRef>
    <Amount>402.00</Amount>
    <AppliedToTxnRet>
      <RefNumber>1880</RefNumber>
      <Amount>402.00</Amount>
      <OpenAmount>0.00</OpenAmount>
    </AppliedToTxnRet>
  </BillPaymentCreditCardRet>
</BillPaymentCreditCardQueryRs></QBXMLMsgsRs></QBXML>";

        var r = _parser.ParseResponse(xml);
        Assert.Single(r.BillPayments);
        Assert.Equal("creditcard", r.BillPayments[0].PaymentMethod);
        Assert.Equal("Pinnacle Drivers Inc", r.BillPayments[0].VendorFullName);
        Assert.Equal(402m, r.BillPayments[0].Amount);
    }
}
