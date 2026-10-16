using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Bson;

namespace ECommerce.Api.Infrastructure;

/// <summary>Converts PascalCase property names to snake_case for API JSON (matches the Python API).</summary>
public sealed class SnakeCaseNamingPolicy : JsonNamingPolicy
{
    public static readonly SnakeCaseNamingPolicy Instance = new();
    public override string ConvertName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }
}

/// <summary>
/// Serializes decimal money values as JSON strings (e.g. "50.16"), matching
/// Pydantic v2's Decimal serialization in the Python API.
/// </summary>
public sealed class DecimalAsStringConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => decimal.Parse(reader.GetString()!, CultureInfo.InvariantCulture),
            JsonTokenType.Number => reader.GetDecimal(),
            _ => throw new JsonException($"Cannot convert {reader.TokenType} to decimal."),
        };

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString("0.00", CultureInfo.InvariantCulture));
}

/// <summary>Decimal money arithmetic: quantize to 2dp, HALF_UP (like Python's quantize).</summary>
public static class Money
{
    /// <summary>Faithful port of Python's Decimal(str(value)).quantize(0.01, HALF_UP).</summary>
    public static decimal FromDouble(double value)
    {
        var d = decimal.Parse(value.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return Math.Round(d, 2, MidpointRounding.AwayFromZero);
    }

    public static decimal Quantize(decimal value)
        => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    public static DateTimeOffset TruncateToSecond(DateTimeOffset dt)
        => new(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, dt.Second, TimeSpan.Zero);
}

/// <summary>HTTP error with an explicit status code; rendered as {"detail": "..."}.</summary>
public sealed class ApiException(int statusCode, string detail) : Exception(detail)
{
    public int StatusCode { get; } = statusCode;
    public string Detail { get; } = detail;
}

/// <summary>Converts ApiException into the {"detail": "..."} error envelope.</summary>
public sealed class ApiExceptionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (ApiException ex)
        {
            context.Response.StatusCode = ex.StatusCode;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { detail = ex.Detail });
        }
    }
}

/// <summary>Converts between BsonValue and plain .NET objects for product attributes/variants.</summary>
public static class BsonConverter
{
    public static object? ToObject(BsonValue v) => v.BsonType switch
    {
        BsonType.String => v.AsString,
        BsonType.Boolean => v.AsBoolean,
        BsonType.Int32 => (long)v.AsInt32,
        BsonType.Int64 => v.AsInt64,
        BsonType.Double => v.AsDouble,
        BsonType.Decimal128 => (double)v.AsDecimal128,
        BsonType.DateTime => v.ToUniversalTime(),
        BsonType.Document => v.AsBsonDocument.ToDictionary(e => e.Name, e => ToObject(e.Value)),
        BsonType.Array => v.AsBsonArray.Select(ToObject).ToList(),
        BsonType.Null or BsonType.Undefined => null,
        BsonType.ObjectId => v.AsObjectId.ToString(),
        _ => v.ToString(),
    };

    public static BsonValue FromObject(object? o) => o switch
    {
        null => BsonNull.Value,
        string s => new BsonString(s),
        bool b => new BsonBoolean(b),
        int i => new BsonInt32(i),
        long l => new BsonInt64(l),
        double d => new BsonDouble(d),
        float f => new BsonDouble(f),
        decimal m => new BsonDouble((double)m),
        DateTime dt => new BsonDateTime(dt.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime()),
        DateTimeOffset dto => new BsonDateTime(dto.UtcDateTime),
        JsonElement e => FromJsonElement(e),
        System.Collections.IDictionary dict => new BsonDocument(
            dict.Keys.Cast<object>().Select(k => new BsonElement(k.ToString()!, FromObject(dict[k])))),
        System.Collections.IEnumerable e => new BsonArray(e.Cast<object>().Select(FromObject)),
        _ => new BsonString(o.ToString()!),
    };

    public static BsonValue FromJsonElement(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.TryGetDateTimeOffset(out var dto)
            ? new BsonDateTime(dto.UtcDateTime) : new BsonString(e.GetString()!),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? new BsonInt64(l) : new BsonDouble(e.GetDouble()),
        JsonValueKind.True => BsonBoolean.True,
        JsonValueKind.False => BsonBoolean.False,
        JsonValueKind.Null or JsonValueKind.Undefined => BsonNull.Value,
        JsonValueKind.Object => new BsonDocument(
            e.EnumerateObject().Select(p => new BsonElement(p.Name, FromJsonElement(p.Value)))),
        JsonValueKind.Array => new BsonArray(e.EnumerateArray().Select(FromJsonElement)),
        _ => BsonNull.Value,
    };
}
