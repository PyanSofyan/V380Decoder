
namespace V380Decoder.src
{
    public class DispatchRequest
    {
        public int dev_id { get; set; }
        public int platform { get; set; }
        public long timestamp { get; set; }
        public string sign { get; set; }
    }
}