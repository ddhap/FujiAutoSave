using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;
using FujiAutoSave.Core.Enums;
using FujiAutoSave.Core.Models;

namespace FujiAutoSave.Core.PTP;

internal static class PtpPacketFactory
{
    private static readonly Encoding _encoding = Encoding.Unicode;
    private const uint PtpInitVersion = 2404639986;
    private const uint PtpInitGuid1 = 1565042093;
    private const uint PtpInitGuid2 = 192918151;
    private const uint PtpInitGuid3 = 3504264659;
    private const uint PtpInitGuid4 = 0;
    private const int PtpInitPacketSize = 82;
    private const int CameraModelNameOffset = 27;
    private const int MinimumCameraModelBufferSize = 39;
    private const int MinimumImageInfoBufferSize = 53;
    private const int FileNameLengthOffset = 52;

    public static string ParseCameraModel(byte[] buffer)
    {
        if (buffer.Length < MinimumCameraModelBufferSize)
        {
            throw new ArgumentException(
                $"Buffer too small: {buffer.Length} bytes, expected at least {MinimumCameraModelBufferSize} bytes",
                nameof(buffer));
        }

        var length = 0;
        for (var i = CameraModelNameOffset; i < buffer.Length - 1; i += 2)
        {
            length = i - CameraModelNameOffset;

            if (buffer[i] == 0 && buffer[i + 1] == 0)
            {
                break;
            }
        }

        return _encoding.GetString(buffer, CameraModelNameOffset + 1, length);
    }

    public static ImageInfo ParseImageInfo(byte[] data)
    {
        if (data.Length < MinimumImageInfoBufferSize)
        {
            throw new ArgumentException(
                $"Buffer too small: {data.Length} bytes, expected at least {MinimumImageInfoBufferSize} bytes",
                nameof(data));
        }

        var imageInfo = new ImageInfo
        {
            StorageId = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0)),
            Format = (ImageFormat)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4)),
            Protection = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6)),
            CompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)),
            MaxPartialSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12)),
            Width = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(26)),
            Height = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(30)),
            BitDepth = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(34)),
            ParentObject = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(38)),
            FileName = string.Empty,
            CreateTime = DateTimeOffset.MinValue
        };

        var offset = FileNameLengthOffset;
        imageInfo.FileName = ReadPtpString(data, ref offset);
        if (string.IsNullOrWhiteSpace(imageInfo.FileName))
        {
            throw new ArgumentException("文件信息中的文件名为空", nameof(data));
        }

        var createTime = ReadPtpString(data, ref offset);
        if (DateTimeOffset.TryParseExact(createTime, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var parsedTime))
        {
            imageInfo.CreateTime = parsedTime;
        }

        return imageInfo;
    }

    private static string ReadPtpString(byte[] data, ref int offset)
    {
        if (offset >= data.Length)
        {
            throw new ArgumentException("文件信息缺少字符串长度字段", nameof(data));
        }

        var characterCount = data[offset++];
        if (characterCount == 0)
        {
            return string.Empty;
        }

        // 长度包含 UTF-16 结束符；按字段边界校验，不能按某一机型的报文总长度校验。
        var byteCount = characterCount * 2;
        if (byteCount > data.Length - offset)
        {
            throw new ArgumentException("文件信息字符串被截断", nameof(data));
        }

        if (data[offset + byteCount - 2] != 0 || data[offset + byteCount - 1] != 0)
        {
            throw new ArgumentException("文件信息字符串缺少结束符", nameof(data));
        }

        var value = _encoding.GetString(data, offset, byteCount - 2);
        offset += byteCount;
        return value;
    }

    public static byte[] CreateInitPacket(IPAddress host, string deviceName)
    {
        var buffer = new byte[PtpInitPacketSize];
        var offset = 0;

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), PtpInitPacketSize);
        offset += 4;

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), (uint)PtpPacketType.Command);
        offset += 4;

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), PtpInitVersion);
        offset += 4;

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), PtpInitGuid1);
        offset += 4;

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), PtpInitGuid2);
        offset += 4;

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), PtpInitGuid3);
        offset += 4;

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), PtpInitGuid4);
        offset += 4;

        var ipBytes = host.GetAddressBytes();
        Array.Copy(ipBytes, 0, buffer, offset, ipBytes.Length);
        offset += 4;

        var nameBytes = _encoding.GetBytes(deviceName);
        Array.Copy(nameBytes, 0, buffer, offset, Math.Min(nameBytes.Length, 50));

        return buffer;
    }

    public static byte[] CreateStatusPacket(uint currentIndex, int totalCount)
    {
        var statusBytes = _encoding.GetBytes($"{currentIndex}/{totalCount}");

        var buffer = new byte[1 + statusBytes.Length + 2];
        buffer[0] = (byte)((statusBytes.Length / 2) + 1);
        Array.Copy(statusBytes, 0, buffer, 1, statusBytes.Length);
        buffer[1 + statusBytes.Length] = 0;
        buffer[1 + statusBytes.Length + 1] = 0;

        return buffer;
    }
}
