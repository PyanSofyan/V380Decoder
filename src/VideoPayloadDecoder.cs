using System.Security.Cryptography;

namespace V380Decoder.src
{
    internal enum VideoDecryptMode
    {
        None,
        Selective64Of80,
        FullBlocks
    }

    internal sealed class VideoPayloadDecoder
    {
        private readonly byte[] aesKey;
        private bool selectionConfirmed;

        public VideoDecryptMode? SelectedMode { get; private set; }
        public int SelectedHeaderSize { get; private set; } = 16;
        public int LastScore { get; private set; } = int.MinValue;

        public VideoPayloadDecoder(byte[] aesKey)
        {
            this.aesKey = aesKey;
        }

        public void Reset(ushort communicationVersion)
        {
            SelectedMode = communicationVersion switch
            {
                21 => VideoDecryptMode.FullBlocks,
                20 => VideoDecryptMode.Selective64Of80,
                _ => null
            };
            SelectedHeaderSize = 16;
            LastScore = int.MinValue;
            selectionConfirmed = false;
        }

        public bool TryDecode(
            byte[] frame,
            VideoCodec codec,
            bool encrypted,
            out byte[] payload,
            out bool selectionChanged)
        {
            if (selectionConfirmed && SelectedMode.HasValue &&
                TryCandidate(
                    frame,
                    codec,
                    SelectedMode.Value,
                    SelectedHeaderSize,
                    out payload,
                    out int confirmedScore) &&
                confirmedScore >= 10)
            {
                LastScore = confirmedScore;
                selectionChanged = false;
                return true;
            }

            selectionConfirmed = false;
            var candidates = new List<(VideoDecryptMode mode, int headerSize)>();

            void AddCandidate(VideoDecryptMode mode, int headerSize)
            {
                if (!candidates.Contains((mode, headerSize)))
                    candidates.Add((mode, headerSize));
            }

            if (encrypted)
            {
                if (SelectedMode.HasValue)
                    AddCandidate(SelectedMode.Value, SelectedHeaderSize);

                foreach (int headerSize in new[] { 16, 20 })
                {
                    AddCandidate(VideoDecryptMode.Selective64Of80, headerSize);
                    AddCandidate(VideoDecryptMode.FullBlocks, headerSize);
                    AddCandidate(VideoDecryptMode.None, headerSize);
                }
            }
            else
            {
                AddCandidate(VideoDecryptMode.None, 16);
                AddCandidate(VideoDecryptMode.None, 20);
            }

            int bestScore = int.MinValue;
            byte[] bestPayload = null;
            VideoDecryptMode bestMode = VideoDecryptMode.None;
            int bestHeaderSize = 16;

            foreach (var candidate in candidates)
            {
                if (!TryCandidate(
                        frame,
                        codec,
                        candidate.mode,
                        candidate.headerSize,
                        out byte[] normalized,
                        out int score))
                    continue;

                if (score <= bestScore) continue;

                bestScore = score;
                bestPayload = normalized;
                bestMode = candidate.mode;
                bestHeaderSize = candidate.headerSize;
            }

            if (bestPayload == null || bestScore < 10)
            {
                payload = Array.Empty<byte>();
                selectionChanged = false;
                LastScore = bestScore;
                return false;
            }

            selectionChanged =
                SelectedMode != bestMode ||
                SelectedHeaderSize != bestHeaderSize;

            SelectedMode = bestMode;
            SelectedHeaderSize = bestHeaderSize;
            LastScore = bestScore;
            selectionConfirmed = true;
            payload = bestPayload;
            return true;
        }

        private bool TryCandidate(
            byte[] frame,
            VideoCodec codec,
            VideoDecryptMode mode,
            int headerSize,
            out byte[] payload,
            out int score)
        {
            if (frame.Length <= headerSize)
            {
                payload = Array.Empty<byte>();
                score = int.MinValue;
                return false;
            }

            var decoded = new byte[frame.Length - headerSize];
            Array.Copy(frame, headerSize, decoded, 0, decoded.Length);
            Decrypt(decoded, mode);

            int leadingBytes = AnnexB.FindStartCode(decoded, 0);
            if (!AnnexB.TryNormalize(decoded, out payload))
            {
                score = int.MinValue;
                return false;
            }

            score = AnnexB.Score(payload, codec) - leadingBytes * 5;
            return score > int.MinValue;
        }

        private void Decrypt(byte[] data, VideoDecryptMode mode)
        {
            if (mode == VideoDecryptMode.None) return;

            using var aes = Aes.Create();
            aes.Key = aesKey;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            using var decryptor = aes.CreateDecryptor();

            if (mode == VideoDecryptMode.FullBlocks)
            {
                int decryptLength = data.Length / 16 * 16;
                if (decryptLength > 0)
                    decryptor.TransformBlock(data, 0, decryptLength, data, 0);
                return;
            }

            for (int offset = 0; offset + 64 <= data.Length; offset += 80)
            {
                for (int block = 0; block < 4; block++)
                {
                    int blockOffset = offset + block * 16;
                    decryptor.TransformBlock(data, blockOffset, 16, data, blockOffset);
                }
            }
        }
    }
}
