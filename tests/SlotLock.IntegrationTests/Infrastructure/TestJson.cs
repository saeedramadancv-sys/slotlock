using System.Text.Json;
using System.Text.Json.Serialization;

namespace SlotLock.IntegrationTests.Infrastructure;

/// <summary>
/// Serializer settings matching what the API is configured to emit.
/// </summary>
/// <remarks>
/// The API writes enums as names. A test client using the framework defaults reads them as
/// ordinals and throws on the way in, which looks like a broken endpoint and is really a
/// mismatched client. Keeping the two in step here means there is one place to change when
/// the wire format does.
/// </remarks>
public static class TestJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}
