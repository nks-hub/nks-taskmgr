using System.Reflection;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace NksTaskMgr.WinUI;

internal enum PublisherPolicy { Broker, Update }
internal sealed record SignedImageProduct(string ProductName, string Description, string OriginalFilename, string ProductVersion);

internal static class PublisherTrust
{
    internal static bool IsConfigured(PublisherPolicy policy) => ReadPin(policy) is not null;
    internal static byte[]? ReadPin(PublisherPolicy policy)
    {
        string key = policy == PublisherPolicy.Broker ? "Nks.BrokerPublisherSpkiSha256" : "Nks.UpdatePublisherSpkiSha256";
        var values = typeof(PublisherTrust).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Where(value => value.Key == key).ToArray();
        string? pin = values.Length == 1 ? values[0].Value : null;
        return pin is { Length: 64 } && pin.All(Uri.IsHexDigit) ? Convert.FromHexString(pin) : null;
    }

    internal static SignedImageProduct ReadProduct(SignedImageLease image)
    {
        try { return ReadProductCore(image); }
        catch (Exception error) when (error is DecoderFallbackException or ArgumentOutOfRangeException or OverflowException) {
            throw new InvalidDataException("Podepsaný prostředek verze PE je poškozený.", error);
        }
    }
    private static SignedImageProduct ReadProductCore(SignedImageLease image)
    {
        var dos = image.ReadAt(0, 64);
        if (U16(dos, 0) != 0x5a4d) throw Invalid();
        uint peOffset = U32(dos, 60);
        if (peOffset is < 64 or > 1024 * 1024) throw Invalid();
        var pe = image.ReadAt(peOffset, 24);
        int sections = U16(pe, 6), optionalBytes = U16(pe, 20);
        if (U32(pe, 0) != 0x4550 || sections is < 1 or > 96 || optionalBytes is < 128 or > 4096 || (U16(pe, 22) & 0x2000) != 0) throw Invalid();
        var optional = image.ReadAt(peOffset + 24, optionalBytes);
        int directory = U16(optional, 0) switch { 0x10b => 96, 0x20b => 112, _ => throw Invalid() };
        if (directory + 24 > optional.Length || U32(optional, directory - 4) < 3) throw Invalid();
        uint rva = U32(optional, directory + 16), size = U32(optional, directory + 20);
        if (rva == 0 || size is < 16 or > 1024 * 1024) throw Invalid();
        var table = image.ReadAt(peOffset + 24 + optionalBytes, sections * 40);
        long fileOffset = -1;
        for (int i = 0; i < sections; ++i) {
            int row = i * 40; uint address = U32(table, row + 12), rawSize = U32(table, row + 16), rawOffset = U32(table, row + 20);
            if (rva < address || rva - address > rawSize || size > rawSize - (rva - address)) continue;
            if (fileOffset >= 0) throw Invalid();
            fileOffset = (long)rawOffset + rva - address;
        }
        if (fileOffset < 0) throw Invalid();
        var resources = image.ReadAt(fileOffset, checked((int)size));
        var root = Directory(resources, 0);
        var types = root.Where(value => value.Id == 16).ToArray();
        if (types.Length != 1 || !types[0].Branch) throw Invalid();
        var names = Directory(resources, types[0].Offset);
        if (names.Length != 1 || !names[0].Branch || names[0].Id != 1) throw Invalid();
        var languages = Directory(resources, names[0].Offset);
        if (languages.Length is 0 or > 16) throw Invalid();
        SignedImageProduct? result = null;
        foreach (var language in languages) {
            if (language.Branch || language.Offset > resources.Length - 16) throw Invalid();
            uint dataRva = U32(resources, language.Offset), dataSize = U32(resources, language.Offset + 4);
            if (dataRva < rva || dataSize is < 64 or > 65535 || dataRva - rva > resources.Length
                || dataSize > resources.Length - (dataRva - rva) || U32(resources, language.Offset + 12) != 0) throw Invalid();
            var product = VersionResource(resources.AsSpan(checked((int)(dataRva - rva)), checked((int)dataSize)).ToArray());
            if (result is not null && result != product) throw new InvalidDataException("Jazykové varianty podepsané role si odporují.");
            result = product;
        }
        image.VerifyUnchanged(); return result ?? throw Invalid();
    }
    internal static SignedImageProduct RequireRole(SignedImageLease image, string description, string originalFilename)
    {
        var product = ReadProduct(image);
        if (product.ProductName != "NKS Task Manager" || product.Description != description || product.OriginalFilename != originalFilename
            || !ValidProductVersion(product.ProductVersion)) throw new InvalidDataException("Podepsaná role nebo verze souboru neodpovídá aplikaci.");
        return product;
    }
    internal static bool ValidProductVersion(string value)
    {
        int plus = value.IndexOf('+');
        if (plus < 5 || value.Length - plus - 1 != 40 || !value.AsSpan(plus + 1).ToArray().All(Uri.IsHexDigit)) return false;
        var parts = value[..plus].Split('.');
        return parts.Length == 3 && parts.All(part => part.Length is > 0 and <= 10 && (part.Length == 1 || part[0] != '0')
            && part.All(char.IsAsciiDigit) && uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _));
    }
    private static SignedImageProduct VersionResource(byte[] bytes)
    {
        var root = Node(bytes, 0, bytes.Length);
        if (root.Key != "VS_VERSION_INFO" || root.Type != 0 || root.ValueBytes != 52 || U32(bytes, root.Value) != 0xfeef04bd) throw Invalid();
        SignedImageProduct? product = null; int tables = 0;
        foreach (var group in Children(bytes, root)) {
            if (group.Key != "StringFileInfo") continue;
            if (group.Type != 1 || group.ValueBytes != 0) throw Invalid();
            foreach (var language in Children(bytes, group)) {
                if (++tables > 16 || language.Type != 1 || language.ValueBytes != 0 || language.Key.Length != 8 || !language.Key.All(Uri.IsHexDigit)) throw Invalid();
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var field in Children(bytes, language)) {
                    if (field.Type != 1 || field.ValueBytes is < 2 or > 4096 || field.ValueBytes % 2 != 0) throw Invalid();
                    string text = Utf16.GetString(bytes.AsSpan(field.Value, field.ValueBytes));
                    if (text[^1] != '\0' || text.AsSpan(0, text.Length - 1).Contains('\0') || !values.TryAdd(field.Key, text[..^1])) throw Invalid();
                }
                if (!values.TryGetValue("ProductName", out var name) || !values.TryGetValue("FileDescription", out var description)
                    || !values.TryGetValue("OriginalFilename", out var original) || !values.TryGetValue("ProductVersion", out var version)) throw Invalid();
                var current = new SignedImageProduct(name, description, original, version);
                if (product is not null && product != current) throw new InvalidDataException("Podepsané verze prostředků si odporují.");
                product = current;
            }
        }
        return product ?? throw Invalid();
    }
    private readonly record struct ResourceEntry(uint Id, int Offset, bool Branch);
    private static ResourceEntry[] Directory(byte[] bytes, int at)
    {
        if (at < 0 || at > bytes.Length - 16) throw Invalid();
        int count = U16(bytes, at + 12) + U16(bytes, at + 14);
        if (count > 512 || count > (bytes.Length - at - 16) / 8) throw Invalid();
        var entries = new ResourceEntry[count];
        for (int i = 0; i < count; ++i) {
            uint name = U32(bytes, at + 16 + i * 8), offset = U32(bytes, at + 20 + i * 8);
            entries[i] = new(name, checked((int)(offset & 0x7fffffff)), (offset & 0x80000000) != 0);
        }
        return entries;
    }
    private readonly record struct VersionNode(string Key, int Type, int Value, int ValueBytes, int Child, int End);
    private static VersionNode Node(byte[] bytes, int at, int end)
    {
        if (at < 0 || end > bytes.Length || at > end - 6) throw Invalid();
        int length = U16(bytes, at), value = U16(bytes, at + 2), type = U16(bytes, at + 4);
        if (length < 8 || length > end - at || type > 1) throw Invalid();
        end = at + length; int textEnd = at + 6;
        while (textEnd <= end - 2 && U16(bytes, textEnd) != 0) textEnd += 2;
        if (textEnd > end - 2 || textEnd - at - 6 > 256) throw Invalid();
        string key = Utf16.GetString(bytes.AsSpan(at + 6, textEnd - at - 6));
        int valueAt = Align(textEnd + 2), valueBytes = type == 1 ? value * 2 : value;
        if (valueAt > end || valueBytes > end - valueAt) throw Invalid();
        return new(key, type, valueAt, valueBytes, Align(valueAt + valueBytes), end);
    }
    private static VersionNode[] Children(byte[] bytes, VersionNode parent)
    {
        var nodes = new List<VersionNode>();
        for (int at = parent.Child; at < parent.End;) {
            if (nodes.Count >= 128) throw Invalid();
            var node = Node(bytes, at, parent.End); nodes.Add(node); at = Align(node.End);
        }
        return nodes.ToArray();
    }
    private static readonly Encoding Utf16 = new UnicodeEncoding(false, false, true);
    private static int Align(int value) => (value + 3) & ~3;
    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static InvalidDataException Invalid() => new("Podepsaný soubor nemá jednoznačné platné prostředky verze PE.");
}
