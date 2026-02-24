namespace V380Decoder.src
{
    public class FrameData
    {
        public byte RawType;   // fragment header type byte (0x00/0x01/0x1A)
        public uint FrameId;
        public ushort FrameType;
        public ushort FrameRate;
        public ulong Timestamp;
        public byte[] Payload;
        public bool IsKeyframe => RawType == 0x00;
    }
}