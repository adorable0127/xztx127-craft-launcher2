// Ported from BedrockLauncher.Core (MIT License, Copyright (c) Round-Studio)
// Source: https://github.com/Round-Studio/BedrockLauncher.Core
namespace XCL2.App.BedrockDecode;

public class CikKey
{
    public const byte MaxSize = 0x30;
    public readonly Guid Guid;
    public byte[] TKey;
    public byte[] DKey;

    public CikKey(ReadOnlySpan<byte> cik)
    {
        this.Guid = new Guid(cik[..0x10]);
        TKey = cik[0x10..0x20].ToArray();
        DKey = cik[0x20..].ToArray();
    }

    public CikKey(string hexString)
    {
        var cik = Convert.FromHexString(hexString);
        this.Guid = new Guid(cik[..0x10]);
        TKey = cik[0x10..0x20].ToArray();
        DKey = cik[0x20..].ToArray();
    }
}