namespace V380Decoder.src
{
    internal static class AnnexB
    {
        public static bool TryNormalize(byte[] data, out byte[] normalized)
        {
            int start = FindStartCode(data, 0);
            if (start < 0 || start > 128)
            {
                normalized = Array.Empty<byte>();
                return false;
            }

            int startCodeLength = GetStartCodeLength(data, start);
            if (startCodeLength == 4 && start == 0)
            {
                normalized = data;
                return true;
            }

            int payloadLength = data.Length - start - startCodeLength;
            if (payloadLength <= 0)
            {
                normalized = Array.Empty<byte>();
                return false;
            }

            normalized = new byte[4 + payloadLength];
            normalized[3] = 1;
            Array.Copy(data, start + startCodeLength, normalized, 4, payloadLength);
            return true;
        }

        public static void Parse(byte[] data, VideoCodec codec, Action<int, byte[]> callback)
        {
            int offset = 0;
            while (offset < data.Length)
            {
                int start = FindStartCode(data, offset);
                if (start < 0) break;

                int nalStart = start + GetStartCodeLength(data, start);
                if (nalStart >= data.Length) break;

                int next = FindStartCode(data, nalStart);
                int nalEnd = next < 0 ? data.Length : next;
                int nalLength = nalEnd - nalStart;
                if (nalLength <= 0)
                {
                    offset = nalStart;
                    continue;
                }

                if (codec == VideoCodec.H265 && nalLength < 2)
                {
                    offset = nalEnd;
                    continue;
                }

                int nalType = codec == VideoCodec.H265
                    ? (data[nalStart] >> 1) & 0x3F
                    : data[nalStart] & 0x1F;

                var nal = new byte[nalLength];
                Array.Copy(data, nalStart, nal, 0, nalLength);
                callback(nalType, nal);
                offset = nalEnd;
            }
        }

        public static int Score(byte[] data, VideoCodec codec)
        {
            int score = 0;
            int count = 0;
            bool invalid = false;

            Parse(data, codec, (type, nal) =>
            {
                count++;

                if (codec == VideoCodec.H264)
                {
                    if (type is <= 0 or >= 24)
                    {
                        invalid = true;
                        return;
                    }

                    score += type switch
                    {
                        7 => 60,
                        8 => 50,
                        5 => 40,
                        _ => 8
                    };
                    return;
                }

                bool validHeader = nal.Length >= 2 &&
                                   (nal[0] & 0x80) == 0 &&
                                   (nal[1] & 0x07) != 0;
                if (!validHeader)
                {
                    invalid = true;
                    return;
                }

                score += type switch
                {
                    32 => 60,                 // VPS
                    33 => HevcSpsParser.TryGetDimensions(nal, out _, out _)
                        ? 270
                        : MarkInvalid(),
                    34 => 60,                 // PPS
                    >= 16 and <= 23 => 45,    // IRAP/IDR/CRA
                    <= 31 => 10,              // VCL
                    <= 47 => 5,               // common non-VCL
                    _ => 1
                };

                int MarkInvalid()
                {
                    invalid = true;
                    return 0;
                }
            });

            if (count == 0 || invalid) return int.MinValue;
            return score + Math.Min(count, 12) * 2;
        }

        public static int FindStartCode(byte[] data, int from)
        {
            for (int i = Math.Max(from, 0); i + 2 < data.Length; i++)
            {
                if (data[i] != 0 || data[i + 1] != 0) continue;
                if (data[i + 2] == 1) return i;
                if (i + 3 < data.Length && data[i + 2] == 0 && data[i + 3] == 1)
                    return i;
            }
            return -1;
        }

        private static int GetStartCodeLength(byte[] data, int offset)
        {
            return offset + 2 < data.Length && data[offset + 2] == 1 ? 3 : 4;
        }
    }
}
