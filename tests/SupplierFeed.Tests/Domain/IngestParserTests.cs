using System.Text.Json.Nodes;
using SupplierFeed.Api.Domain;

namespace SupplierFeed.Tests.Domain;

[TestFixture]
public class IngestParserTests
{
    private static JsonObject ValidBody() => new()
    {
        ["supplierId"] = "s1",
        ["reservationId"] = "r1",
        ["roomId"] = "room-1",
        ["checkIn"] = "2026-08-01T00:00:00Z",
        ["checkOut"] = "2026-08-03T00:00:00Z",
        ["price"] = 450.00m,
        ["updatedAtUtc"] = "2026-07-01T10:15:00Z",
    };

    private static long Ms(string utcIso) => DateTimeOffset.Parse(utcIso).ToUnixTimeMilliseconds();

    private static IngestParseResult Parse(JsonObject body) => IngestParser.Parse(body.ToJsonString());

    private static IngestParseResult.Invalid AssertInvalid(IngestParseResult result)
    {
        Assert.That(result, Is.InstanceOf<IngestParseResult.Invalid>());
        return (IngestParseResult.Invalid)result;
    }

    private static ValidatedIngest AssertValid(IngestParseResult result)
    {
        Assert.That(result, Is.InstanceOf<IngestParseResult.Valid>());
        return ((IngestParseResult.Valid)result).Request;
    }

    // ---- unattributable: no usable supplierId, so nothing can be throttled ----

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("not json")]
    [TestCase("[]")]
    [TestCase("\"just a string\"")]
    [TestCase("null")]
    [TestCase("{}")]
    [TestCase("{\"supplierId\":\"\"}")]
    [TestCase("{\"supplierId\":\"   \"}")]
    [TestCase("{\"supplierId\":42}")]
    [TestCase("{\"supplierId\":null}")]
    public void Body_without_a_usable_supplier_id_is_unattributable(string body)
    {
        Assert.That(IngestParser.Parse(body), Is.InstanceOf<IngestParseResult.Unattributable>());
    }

    [Test]
    public void Property_names_are_case_insensitive()
    {
        var body = new JsonObject { ["SupplierId"] = "s1" };

        var invalid = AssertInvalid(Parse(body));

        Assert.That(invalid.SupplierId, Is.EqualTo("s1"));
    }

    // ---- valid ----

    [Test]
    public void Valid_body_is_parsed_with_utc_milliseconds()
    {
        var request = AssertValid(Parse(ValidBody()));

        Assert.That(request.SupplierId, Is.EqualTo("s1"));
        Assert.That(request.ReservationId, Is.EqualTo("r1"));
        Assert.That(request.RoomId, Is.EqualTo("room-1"));
        Assert.That(request.CheckInMs, Is.EqualTo(Ms("2026-08-01T00:00:00Z")));
        Assert.That(request.CheckOutMs, Is.EqualTo(Ms("2026-08-03T00:00:00Z")));
        Assert.That(request.UpdatedAtMs, Is.EqualTo(Ms("2026-07-01T10:15:00Z")));
        Assert.That(request.Price, Is.EqualTo(450.00m));
    }

    [Test]
    public void Offset_timestamps_are_converted_to_utc()
    {
        var body = ValidBody();
        body["checkIn"] = "2026-08-01T02:00:00+02:00";

        Assert.That(AssertValid(Parse(body)).CheckInMs, Is.EqualTo(Ms("2026-08-01T00:00:00Z")));
    }

    [Test]
    public void Timestamp_without_offset_is_treated_as_utc_not_server_local_time()
    {
        var body = ValidBody();
        body["checkIn"] = "2026-08-01T00:00:00";

        Assert.That(AssertValid(Parse(body)).CheckInMs, Is.EqualTo(Ms("2026-08-01T00:00:00Z")));
    }

    [Test]
    public void Fractional_seconds_are_kept_to_the_millisecond()
    {
        var body = ValidBody();
        body["updatedAtUtc"] = "2026-07-01T10:15:00.123Z";

        Assert.That(AssertValid(Parse(body)).UpdatedAtMs, Is.EqualTo(Ms("2026-07-01T10:15:00Z") + 123));
    }

    [Test]
    public void Price_keeps_full_decimal_precision()
    {
        var body = ValidBody();
        body["price"] = 450.1000000000000001m;

        Assert.That(AssertValid(Parse(body)).Price, Is.EqualTo(450.1000000000000001m));
    }

    [Test]
    public void Zero_price_is_valid()
    {
        var body = ValidBody();
        body["price"] = 0m;

        Assert.That(AssertValid(Parse(body)).Price, Is.Zero);
    }

    // ---- invalid: attributable, counts toward the limit, reported as 400 ----

    [TestCase("reservationId")]
    [TestCase("roomId")]
    [TestCase("checkIn")]
    [TestCase("checkOut")]
    [TestCase("price")]
    [TestCase("updatedAtUtc")]
    public void Missing_required_field_is_invalid_and_names_the_field(string field)
    {
        var body = ValidBody();
        body.Remove(field);

        var invalid = AssertInvalid(Parse(body));

        Assert.That(invalid.SupplierId, Is.EqualTo("s1"));
        Assert.That(invalid.Errors, Has.Count.EqualTo(1));
        Assert.That(invalid.Errors[0], Does.Contain(field));
    }

    [TestCase("reservationId")]
    [TestCase("roomId")]
    public void Blank_text_field_is_invalid(string field)
    {
        var body = ValidBody();
        body[field] = "  ";

        Assert.That(AssertInvalid(Parse(body)).Errors.Single(), Does.Contain(field));
    }

    [Test]
    public void Negative_price_is_invalid()
    {
        var body = ValidBody();
        body["price"] = -0.01m;

        Assert.That(AssertInvalid(Parse(body)).Errors.Single(), Does.Contain("price"));
    }

    [Test]
    public void Non_numeric_price_is_invalid_but_still_attributable()
    {
        var body = ValidBody();
        body["price"] = "abc";

        var invalid = AssertInvalid(Parse(body));

        Assert.That(invalid.SupplierId, Is.EqualTo("s1"));
        Assert.That(invalid.Errors.Single(), Does.Contain("price"));
    }

    [TestCase("yesterday")]
    [TestCase("2026-08-01")]
    [TestCase("08/01/2026")]
    public void Unparsable_timestamp_is_invalid(string value)
    {
        var body = ValidBody();
        body["checkIn"] = value;

        Assert.That(AssertInvalid(Parse(body)).Errors.Single(), Does.Contain("checkIn"));
    }

    [Test]
    public void Numeric_timestamp_is_invalid()
    {
        var body = ValidBody();
        body["updatedAtUtc"] = 1790000000;

        Assert.That(AssertInvalid(Parse(body)).Errors.Single(), Does.Contain("updatedAtUtc"));
    }

    [TestCase("2026-08-01T00:00:00Z")] // equal
    [TestCase("2026-07-31T00:00:00Z")] // before
    public void Check_out_must_be_after_check_in(string checkOut)
    {
        var body = ValidBody();
        body["checkOut"] = checkOut;

        Assert.That(AssertInvalid(Parse(body)).Errors.Single(), Does.Contain("checkOut"));
    }

    [Test]
    public void All_errors_are_reported_together()
    {
        var body = ValidBody();
        body.Remove("roomId");
        body["price"] = -5m;

        var invalid = AssertInvalid(Parse(body));

        Assert.That(invalid.Errors, Has.Count.EqualTo(2));
    }

    [Test]
    public void Date_order_is_not_checked_when_a_date_is_unparsable()
    {
        var body = ValidBody();
        body["checkIn"] = "nonsense";

        Assert.That(AssertInvalid(Parse(body)).Errors, Has.Count.EqualTo(1));
    }
}
