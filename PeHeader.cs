namespace Sweep;

/// <summary>Minimal PE header reader, used only to tell 32-bit from 64-bit programs.</summary>
internal static class PeHeader
{
    private const ushort MachineI386 = 0x014C;
    private const ushort MachineArm64 = 0xAA64;
    private const ushort MachineAmd64 = 0x8664;
    private const ushort MachineArm = 0x01C4;

    /// <summary>Returns "32-bit", "64-bit" or null when the file cannot be read.</summary>
    public static string? ReadArchitecture(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(stream);

            if (stream.Length < 0x40)
            {
                return null;
            }

            stream.Position = 0x3C;
            int peOffset = reader.ReadInt32();

            // The MZ stub guarantees e_lfanew sits inside the DOS header.
            if (peOffset <= 0 || peOffset > stream.Length - 6)
            {
                return null;
            }

            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x0000_4550) // "PE\0\0"
            {
                return null;
            }

            ushort machine = reader.ReadUInt16();

            return machine switch
            {
                MachineI386 or MachineArm => "32-bit",
                MachineAmd64 or MachineArm64 => "64-bit",
                _ => null,
            };
        }
        catch
        {
            // Locked, missing, or not a PE file (an MSI database, for example).
            return null;
        }
    }
}