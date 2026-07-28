using System.Security.Cryptography;
using V380Decoder.src;
using Xunit;

namespace V380Decoder.Tests;

public class VideoPayloadDecoderTests
{
    [Theory]
    [InlineData((int)VideoDecryptMode.Selective64Of80, 16)]
    [InlineData((int)VideoDecryptMode.FullBlocks, 16)]
    [InlineData((int)VideoDecryptMode.Selective64Of80, 20)]
    [InlineData((int)VideoDecryptMode.FullBlocks, 20)]
    public void AutoDetectsHevcEncryptionAndHeader(
        int encryptionModeValue,
        int headerSize)
    {
        var encryptionMode = (VideoDecryptMode)encryptionModeValue;
        byte[] key = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        byte[] original = BuildHevcAccessUnit();
        byte[] encrypted = Encrypt(original, key, encryptionMode);
        byte[] frame = new byte[headerSize + encrypted.Length];
        Array.Copy(encrypted, 0, frame, headerSize, encrypted.Length);

        var decoder = new VideoPayloadDecoder(key);
        decoder.Reset(communicationVersion: 0);

        bool success = decoder.TryDecode(
            frame,
            VideoCodec.H265,
            encrypted: true,
            out byte[] decoded,
            out _);

        Assert.True(success);
        Assert.Equal(original, decoded);
        Assert.Equal(encryptionMode, decoder.SelectedMode);
        Assert.Equal(headerSize, decoder.SelectedHeaderSize);
    }

    [Fact]
    public void CommunicationVersion21UsesFullBlockDecryption()
    {
        byte[] key = Enumerable.Range(17, 16).Select(i => (byte)i).ToArray();
        byte[] original = BuildHevcAccessUnit();
        byte[] encrypted = Encrypt(original, key, VideoDecryptMode.FullBlocks);
        byte[] frame = new byte[16 + encrypted.Length];
        Array.Copy(encrypted, 0, frame, 16, encrypted.Length);

        var decoder = new VideoPayloadDecoder(key);
        decoder.Reset(communicationVersion: 21);

        Assert.True(decoder.TryDecode(
            frame,
            VideoCodec.H265,
            encrypted: true,
            out byte[] decoded,
            out _));
        Assert.Equal(original, decoded);
        Assert.Equal(VideoDecryptMode.FullBlocks, decoder.SelectedMode);
    }

    [Fact]
    public void RejectsPayloadWithoutAnnexBUnits()
    {
        byte[] key = new byte[16];
        var decoder = new VideoPayloadDecoder(key);
        decoder.Reset(communicationVersion: 0);

        Assert.False(decoder.TryDecode(
            Enumerable.Repeat((byte)0xAA, 512).ToArray(),
            VideoCodec.H265,
            encrypted: true,
            out _,
            out _));
    }

    [Fact]
    public void ParsesDimensionsFromRealHevcSps()
    {
        byte[] sps = null;
        AnnexB.Parse(
            BuildHevcAccessUnit(),
            VideoCodec.H265,
            (type, nal) =>
            {
                if (type == 33) sps = nal;
            });

        Assert.NotNull(sps);
        Assert.True(HevcSpsParser.TryGetDimensions(sps, out int width, out int height));
        Assert.Equal(64, width);
        Assert.Equal(64, height);
    }

    private static byte[] BuildHevcAccessUnit()
    {
        // One real 64x64 x265 keyframe with VPS/SPS/PPS and no metadata SEI.
        return Convert.FromBase64String(
            "AAAAAUABDAH//wFgAAADAJAAAAMAAAMAHpWYCQAAAAFCAQEBYAAAAwCQ" +
            "AAADAAADAB6gIIEFllZpJMrwFoCAAAADAIAAAAMAhAAAAAFEAcFytCJ" +
            "AAAABKAGvE4DmaOP//RfPx/bP");
    }

    private static byte[] Encrypt(
        byte[] plaintext,
        byte[] key,
        VideoDecryptMode mode)
    {
        byte[] encrypted = (byte[])plaintext.Clone();
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor();

        if (mode == VideoDecryptMode.FullBlocks)
        {
            int length = encrypted.Length / 16 * 16;
            encryptor.TransformBlock(encrypted, 0, length, encrypted, 0);
            return encrypted;
        }

        for (int offset = 0; offset + 64 <= encrypted.Length; offset += 80)
        {
            for (int block = 0; block < 4; block++)
            {
                int blockOffset = offset + block * 16;
                encryptor.TransformBlock(
                    encrypted, blockOffset, 16, encrypted, blockOffset);
            }
        }

        return encrypted;
    }
}
