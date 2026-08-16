using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.BuildingBlocks.Domain.Primitives;
using BusinessOS.Modules.Budgeting.Domain;
using Xunit;
namespace BusinessOS.UnitTests;

public sealed class SupplierInvoiceDomainTests
{
    private static SupplierInvoice Make(string s = " Acme ", string n = " INV-1 ", decimal a = 1, DateOnly i = default, DateOnly d = default, string? note = " note ") => SupplierInvoice.Create(BusinessProjectId.New(), s, n, new(a, new CurrencyCode("PLN")), i == default ? new(2026, 1, 1) : i, d == default ? new(2026, 1, 2) : d, note, DateTimeOffset.UnixEpoch);
    [Fact] public void Create_trims_and_normalizes_identity_and_note() { var x = Make(); Assert.Equal("Acme", x.SupplierName); Assert.Equal("ACME", x.SupplierKey); Assert.Equal("INV-1", x.InvoiceNumber); Assert.Equal("note", x.Note); }
    [Fact] public void Whitespace_note_becomes_null() => Assert.Null(Make(note: " ").Note);
    [Fact] public void Invalid_amount_is_rejected() => Assert.Throws<ArgumentOutOfRangeException>(() => Make(a: 0));
    [Fact] public void Due_before_invoice_is_rejected() => Assert.Throws<ArgumentException>(() => Make(i: new(2026, 2, 1), d: new(2026, 1, 1)));
    [Fact] public void Update_and_archive_increment_version() { var x = Make(); x.Update("Acme", "I", new(2, new("PLN")), new(2026, 1, 1), new(2026, 1, 1), null, DateTimeOffset.UnixEpoch.AddDays(1)); Assert.Equal(2, x.Version); x.Archive(DateTimeOffset.UnixEpoch.AddDays(2)); Assert.Equal(3, x.Version); Assert.NotNull(x.ArchivedAtUtc); }
}
