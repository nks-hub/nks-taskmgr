using System.Buffers.Binary;
using System.Text;

namespace NksTaskMgr.WinUI;

internal static class PublisherTrustChecks
{
    private const string Version = "1.2.3+aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal static void Run()
    {
        const string setup = "NKS Task Manager Setup", setupName = "NksTaskMgrSetup.exe";
        var padded = Resource(setup, setupName, "   ", secondPadding: " ");
        var product = PublisherTrust.RequireVersionResource(padded, setup, setupName);
        Require(product == new SignedImageProduct("NKS Task Manager", setup, setupName, Version), "Installer ASCII padding was not normalized before role/language validation.");
        foreach (var role in new[] { ("NKS Task Manager", "NksTaskMgr.WinUI.dll"), ("NKS Task Manager Update Worker", "nks-update-worker.exe") }) {
            var exact = PublisherTrust.RequireVersionResource(Resource(role.Item1, role.Item2, ""), role.Item1, role.Item2);
            Require(exact.ProductVersion == Version, "An exact GUI/helper role was rejected.");
            foreach (string field in new[] { "ProductName", "FileDescription", "OriginalFilename", "ProductVersion" })
                Reject(Resource(role.Item1, role.Item2, " ", onlyField: field), role.Item1, role.Item2);
        }
        foreach (string suffix in new[] { "\t", "\u00a0", " \t " }) Reject(Resource(setup, setupName, suffix), setup, setupName);
        Reject(Resource(setup, setupName, "", prefix: " "), setup, setupName);
        Reject(Resource(setup + "X", setupName, " "), setup, setupName);
        Reject(Resource(setup, setupName, "\0 "), setup, setupName);
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(byte[] bytes, string description, string filename)
    {
        try { PublisherTrust.RequireVersionResource(bytes, description, filename); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Padded non-installer, non-ASCII, leading-space or malformed metadata was accepted.");
    }
    private static byte[] Resource(string description, string filename, string padding, string? secondPadding = null, string? onlyField = null, string prefix = "")
    {
        byte[] Table(string language, string suffix)
        {
            var fields = new[] { ("ProductName", "NKS Task Manager"), ("FileDescription", description), ("OriginalFilename", filename), ("ProductVersion", Version) };
            return Node(language, [], fields.Select(field => Node(field.Item1,
                Encoding.Unicode.GetBytes(prefix + field.Item2 + (onlyField is null || onlyField == field.Item1 ? suffix : "") + '\0'), [], text: true)).ToArray(), text: true);
        }
        byte[] fixedInfo = new byte[52]; BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo, 0xfeef04bd);
        var tables = secondPadding is null ? new[] { Table("040904b0", padding) }
            : new[] { Table("040904b0", padding), Table("040504b0", secondPadding) };
        return Node("VS_VERSION_INFO", fixedInfo, [Node("StringFileInfo", [], tables, text: true)]);
    }
    private static byte[] Node(string key, byte[] value, byte[][] children, bool text = false)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true);
        writer.Write((ushort)0); writer.Write(checked((ushort)(text ? value.Length / 2 : value.Length))); writer.Write((ushort)(text ? 1 : 0));
        writer.Write(Encoding.Unicode.GetBytes(key + '\0')); Align(); writer.Write(value);
        foreach (var child in children) { Align(); writer.Write(child); }
        var bytes = stream.ToArray(); BinaryPrimitives.WriteUInt16LittleEndian(bytes, checked((ushort)bytes.Length)); return bytes;
        void Align() { while ((stream.Length & 3) != 0) writer.Write((byte)0); }
    }
}
