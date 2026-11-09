using System.Text.Json;

namespace NksTaskMgr.WinUI;

public static partial class WinUiChecks
{
    internal static void OnlineUpdateRelease()
    {
        const string setup = "https://github.com/nks-hub/nks-taskmgr/releases/download/v0.3.0/NksTaskMgr-0.3.0-0123abc-win-x64-setup.exe";
        string Release(string tag, string name, string url, bool prerelease = false) => $$"""
            {"tag_name":"{{tag}}","draft":false,"prerelease":{{(prerelease ? "true" : "false")}},"html_url":"https://github.com/nks-hub/nks-taskmgr/releases/tag/{{tag}}",
             "assets":[{"name":"NksTaskMgr-0.3.0-0123abc-win-x64.zip","browser_download_url":"https://github.com/nks-hub/nks-taskmgr/releases/download/{{tag}}/x.zip","size":10},
                       {"name":"{{name}}","browser_download_url":"{{url}}","size":1000}]}
            """;
        OnlineRelease? Parse(string json, string current) {
            using var document = JsonDocument.Parse(json);
            return OnlineUpdate.Parse(document.RootElement, OnlineUpdate.ParseVersion(current)!);
        }
        var found = Parse(Release("v0.3.0", "NksTaskMgr-0.3.0-0123abc-win-x64-setup.exe", setup), "0.2.10");
        Require(found is { Size: 1000 } && found.Version == new Version(0, 3, 0) && found.SetupUrl.AbsoluteUri == setup, "A newer release did not select its x64 setup.");
        Require(Parse(Release("v0.3.0", "NksTaskMgr-0.3.0-0123abc-win-x64-setup.exe", setup), "0.3.0") is null, "The installed version was offered as an update.");
        Require(Parse(Release("v0.3.0", "NksTaskMgr-0.3.0-0123abc-win-x64-setup.exe", setup, true), "0.2.0") is null, "A prerelease was offered as an update.");
        Require(OnlineUpdate.ParseVersion("0.2.10") > OnlineUpdate.ParseVersion("0.2.9")!, "Versions compared as text.");
        Require(OnlineUpdate.ParseVersion("1.2") is null && OnlineUpdate.ParseVersion("1.2.3.4") is null, "A malformed version was accepted.");
        bool Rejected(string json) { try { Parse(json, "0.2.0"); return false; } catch (InvalidDataException) { return true; } }
        Require(Rejected(Release("v0.3.0", "NksTaskMgr-0.3.0-0123abc-win-x64-setup.exe", "https://example.com/nks-hub/nks-taskmgr/releases/download/v0.3.0/a.exe")),
            "A setup hosted outside the repository was accepted.");
        Require(Rejected(Release("v0.3.0", "NksTaskMgr-0.2.9-0123abc-win-x64-setup.exe", setup)), "A setup of another version was accepted.");
        App.Trace("Online update release parsing passed: newer x64 setup, same version, prerelease, numeric order, foreign host and version mismatch");
    }
}
