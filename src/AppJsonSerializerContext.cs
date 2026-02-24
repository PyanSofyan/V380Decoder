using System.Text.Json.Serialization;

namespace V380Decoder.src
{
    [JsonSerializable(typeof(DispatchRequest))]
    [JsonSerializable(typeof(DispatchResult))]
    internal partial class AppJsonSerializerContext : JsonSerializerContext
    {
    }
}