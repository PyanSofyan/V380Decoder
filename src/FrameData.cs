namespace V380Decoder.src
{
    public class FrameData
    {
        public byte RawType;
        public uint FrameId;
        public ushort FrameType;
        public ushort FrameRate;
        public ulong Timestamp;
        public VideoCodec Codec = VideoCodec.H264;
        public byte[] Payload;
        public bool IsKeyframe => RawType is 0x00 or 0x28;
    }
}
