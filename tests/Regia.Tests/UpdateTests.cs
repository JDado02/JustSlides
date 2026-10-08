using Regia.Core.Update;

namespace Regia.Tests;

public class UpdateTests
{
    [Theory]
    [InlineData("1.1.0", 1, 1, 0)]
    [InlineData("v1.1.0", 1, 1, 0)]
    [InlineData("V2.10.3", 2, 10, 3)]
    [InlineData("1.1.0.0", 1, 1, 0)]
    [InlineData("1.1.1+abc123", 1, 1, 1)]
    [InlineData("1.2", 1, 2, 0)]
    public void TryParse_AcceptsKnownFormats(string text, int major, int minor, int patch)
    {
        Assert.True(ReleaseVersion.TryParse(text, out var version));
        Assert.Equal(new ReleaseVersion(major, minor, patch), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("1")]
    [InlineData("1.1.0-beta")]
    [InlineData("1.x.0")]
    [InlineData("1.1.0.5")]
    [InlineData("-1.1.0")]
    public void TryParse_RejectsOthers(string? text)
    {
        Assert.False(ReleaseVersion.TryParse(text, out _));
    }

    [Theory]
    [InlineData("1.1.1", "1.1.0", true)]
    [InlineData("1.2.0", "1.1.9", true)]
    [InlineData("2.0.0", "1.99.99", true)]
    [InlineData("1.1.0", "1.1.0", false)]
    [InlineData("1.0.9", "1.1.0", false)]
    [InlineData("1.10.0", "1.9.0", true)]   // numeri, non stringhe
    public void IsNewerThan(string candidate, string current, bool expected)
    {
        Assert.True(ReleaseVersion.TryParse(candidate, out var a));
        Assert.True(ReleaseVersion.TryParse(current, out var b));
        Assert.Equal(expected, a.IsNewerThan(b));
    }

    private const string Sha = "500D6DE610BD4C918282403A9E445B154A8C312170A6417587D6E760E8D2B111";

    private static string Json(string tag = "v1.1.1", string asset = "JustSlides-Setup.exe", bool draft = false, bool pre = false, string body = "Novita.\\n\\nSHA256: " + Sha) =>
        "{\"tag_name\":\"" + tag + "\",\"draft\":" + (draft ? "true" : "false") + ",\"prerelease\":" + (pre ? "true" : "false") +
        ",\"body\":\"" + body + "\",\"assets\":[{\"name\":\"" + asset + "\",\"size\":89000000," +
        "\"browser_download_url\":\"https://github.com/JDado02/JustSlides/releases/download/" + tag + "/" + asset + "\"}]}";

    [Fact]
    public void Parse_ReadsRelease()
    {
        var release = UpdateRelease.Parse(Json());

        Assert.NotNull(release);
        Assert.Equal(new ReleaseVersion(1, 1, 1), release.Version);
        Assert.Equal("v1.1.1", release.Tag);
        Assert.Equal(Sha, release.Sha256);
        Assert.Equal(89000000, release.Size);
        Assert.EndsWith("/v1.1.1/JustSlides-Setup.exe", release.DownloadUrl);
        Assert.Contains("Novita", release.Notes);
    }

    [Fact]
    public void Parse_Rejects_DraftPrereleaseWrongAssetBadTagAndGarbage()
    {
        Assert.Null(UpdateRelease.Parse(Json(draft: true)));
        Assert.Null(UpdateRelease.Parse(Json(pre: true)));
        Assert.Null(UpdateRelease.Parse(Json(asset: "altro.zip")));
        Assert.Null(UpdateRelease.Parse(Json(tag: "nightly")));
        Assert.Null(UpdateRelease.Parse("non è json"));
        Assert.Null(UpdateRelease.Parse("[]"));
        Assert.Null(UpdateRelease.Parse("{\"tag_name\":\"v1.2.0\"}"));
    }

    [Fact]
    public void Parse_WithoutShaInNotes_HasNullSha()
    {
        Assert.Null(UpdateRelease.Parse(Json(body: "solo testo"))!.Sha256);
    }

    [Theory]
    [InlineData("SHA256: 500d6de610bd4c918282403a9e445b154a8c312170a6417587d6e760e8d2b111")]
    [InlineData("sha-256 = 500D6DE610BD4C918282403A9E445B154A8C312170A6417587D6E760E8D2B111")]
    [InlineData("xx\\nSHA256:500D6DE610BD4C918282403A9E445B154A8C312170A6417587D6E760E8D2B111\\nfine")]
    public void ParseSha256_IsTolerantAndUppercases(string notes)
    {
        Assert.Equal(Sha, UpdateRelease.ParseSha256(notes.Replace("\\n", "\n")));
    }

    [Fact]
    public void ParseSha256_RejectsWrongLength()
    {
        Assert.Null(UpdateRelease.ParseSha256("SHA256: 1234"));
        Assert.Null(UpdateRelease.ParseSha256(null));
    }
}
