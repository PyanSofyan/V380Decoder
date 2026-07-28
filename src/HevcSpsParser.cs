namespace V380Decoder.src
{
    internal static class HevcSpsParser
    {
        public static bool TryGetDimensions(byte[] nal, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (nal == null || nal.Length < 4 || ((nal[0] >> 1) & 0x3F) != 33)
                return false;

            byte[] rbsp = RemoveEmulationPreventionBytes(nal, 2);
            var bits = new BitReader(rbsp);

            if (!bits.TryReadBits(4, out _) ||
                !bits.TryReadBits(3, out uint maxSubLayersMinus1) ||
                maxSubLayersMinus1 > 6 ||
                !bits.TryReadBits(1, out _) ||
                !TrySkipProfileTierLevel(bits, (int)maxSubLayersMinus1) ||
                !bits.TryReadUnsignedExpGolomb(out uint spsId) ||
                spsId > 15 ||
                !bits.TryReadUnsignedExpGolomb(out uint chromaFormatIdc) ||
                chromaFormatIdc > 3)
                return false;

            bool separateColourPlane = false;
            if (chromaFormatIdc == 3)
            {
                if (!bits.TryReadBits(1, out uint separate)) return false;
                separateColourPlane = separate != 0;
            }

            if (!bits.TryReadUnsignedExpGolomb(out uint codedWidth) ||
                !bits.TryReadUnsignedExpGolomb(out uint codedHeight) ||
                codedWidth == 0 ||
                codedHeight == 0 ||
                codedWidth > 32768 ||
                codedHeight > 32768 ||
                !bits.TryReadBits(1, out uint conformanceWindowFlag))
                return false;

            uint cropLeft = 0;
            uint cropRight = 0;
            uint cropTop = 0;
            uint cropBottom = 0;
            if (conformanceWindowFlag != 0 &&
                (!bits.TryReadUnsignedExpGolomb(out cropLeft) ||
                 !bits.TryReadUnsignedExpGolomb(out cropRight) ||
                 !bits.TryReadUnsignedExpGolomb(out cropTop) ||
                 !bits.TryReadUnsignedExpGolomb(out cropBottom)))
                return false;

            uint chromaArrayType = separateColourPlane ? 0 : chromaFormatIdc;
            uint subWidth = chromaArrayType is 1 or 2 ? 2u : 1u;
            uint subHeight = chromaArrayType == 1 ? 2u : 1u;
            ulong horizontalCrop = (ulong)subWidth * (cropLeft + cropRight);
            ulong verticalCrop = (ulong)subHeight * (cropTop + cropBottom);
            if (horizontalCrop >= codedWidth || verticalCrop >= codedHeight)
                return false;

            uint displayWidth = codedWidth - (uint)horizontalCrop;
            uint displayHeight = codedHeight - (uint)verticalCrop;

            if (!bits.TryReadUnsignedExpGolomb(out uint bitDepthLumaMinus8) ||
                !bits.TryReadUnsignedExpGolomb(out uint bitDepthChromaMinus8) ||
                !bits.TryReadUnsignedExpGolomb(out uint log2MaxPocLsbMinus4) ||
                bitDepthLumaMinus8 > 8 ||
                bitDepthChromaMinus8 > 8 ||
                log2MaxPocLsbMinus4 > 12 ||
                displayWidth < 16 ||
                displayHeight < 16)
                return false;

            width = (int)displayWidth;
            height = (int)displayHeight;
            return true;
        }

        private static bool TrySkipProfileTierLevel(BitReader bits, int maxSubLayersMinus1)
        {
            if (!bits.TrySkipBits(88) || !bits.TryReadBits(8, out uint levelIdc) || levelIdc == 0)
                return false;

            var profilePresent = new bool[maxSubLayersMinus1];
            var levelPresent = new bool[maxSubLayersMinus1];
            for (int i = 0; i < maxSubLayersMinus1; i++)
            {
                if (!bits.TryReadBits(1, out uint profile) ||
                    !bits.TryReadBits(1, out uint level))
                    return false;
                profilePresent[i] = profile != 0;
                levelPresent[i] = level != 0;
            }

            if (maxSubLayersMinus1 > 0)
            {
                for (int i = maxSubLayersMinus1; i < 8; i++)
                {
                    if (!bits.TryReadBits(2, out uint reserved) || reserved != 0)
                        return false;
                }
            }

            for (int i = 0; i < maxSubLayersMinus1; i++)
            {
                if (profilePresent[i] && !bits.TrySkipBits(88))
                    return false;
                if (levelPresent[i] && !bits.TrySkipBits(8))
                    return false;
            }

            return true;
        }

        private static byte[] RemoveEmulationPreventionBytes(byte[] data, int offset)
        {
            var rbsp = new List<byte>(data.Length - offset);
            int zeroCount = 0;
            for (int i = offset; i < data.Length; i++)
            {
                byte value = data[i];
                if (zeroCount >= 2 &&
                    value == 0x03 &&
                    i + 1 < data.Length &&
                    data[i + 1] <= 0x03)
                {
                    zeroCount = 0;
                    continue;
                }

                rbsp.Add(value);
                zeroCount = value == 0 ? zeroCount + 1 : 0;
            }
            return rbsp.ToArray();
        }

        private sealed class BitReader
        {
            private readonly byte[] data;
            private int bitOffset;

            public BitReader(byte[] data)
            {
                this.data = data;
            }

            public bool TryReadBits(int count, out uint value)
            {
                value = 0;
                if (count is < 0 or > 32 || bitOffset + count > data.Length * 8)
                    return false;

                for (int i = 0; i < count; i++)
                {
                    int byteOffset = bitOffset >> 3;
                    int shift = 7 - (bitOffset & 7);
                    value = (value << 1) | (uint)((data[byteOffset] >> shift) & 1);
                    bitOffset++;
                }
                return true;
            }

            public bool TrySkipBits(int count)
            {
                if (count < 0 || bitOffset + count > data.Length * 8)
                    return false;
                bitOffset += count;
                return true;
            }

            public bool TryReadUnsignedExpGolomb(out uint value)
            {
                value = 0;
                int leadingZeros = 0;
                while (true)
                {
                    if (!TryReadBits(1, out uint bit)) return false;
                    if (bit != 0) break;
                    leadingZeros++;
                    if (leadingZeros > 31) return false;
                }

                if (leadingZeros == 0) return true;
                if (!TryReadBits(leadingZeros, out uint suffix)) return false;

                value = ((1u << leadingZeros) - 1u) + suffix;
                return true;
            }
        }
    }
}
