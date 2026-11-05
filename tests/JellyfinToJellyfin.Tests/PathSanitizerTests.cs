using JellyfinToJellyfin.Services;

namespace JellyfinToJellyfin.Tests;

public class PathSanitizerTests
{
    [Theory]
    [InlineData("Mission: Impossible - Fallout", "Mission_ Impossible - Fallout")]
    [InlineData("  Spider-Man/No Way Home  ", "Spider-Man_No Way Home")]
    [InlineData("Bad\\Name*With?Chars\"<>|", "Bad_Name_With_Chars____")]
    [InlineData("a\t\tb", "a__b")]
    [InlineData("a   b", "a b")]
    [InlineData("Name ...   ", "Name")]
    [InlineData("a\u0001b", "a_b")]
    public void Sanitize_ReplacesUnsafeCharactersAndCollapsesWhitespace(string input, string expected)
        => Assert.Equal(expected, PathSanitizer.Sanitize(input));

    [Fact]
    public void Sanitize_UsesFallbackForEmptyInput()
    {
        Assert.Equal("Unknown", PathSanitizer.Sanitize(null));
        Assert.Equal("Unknown", PathSanitizer.Sanitize("   "));
        Assert.Equal("Library", PathSanitizer.Sanitize(null, "Library"));
    }

    [Fact]
    public void Sanitize_TrimsNamesLongerThan120Characters()
    {
        var name = new string('x', 200);

        var sanitized = PathSanitizer.Sanitize(name);

        Assert.Equal(120, sanitized.Length);
        Assert.Equal(new string('x', 120), sanitized);
    }

    [Fact]
    public void Sanitize_NeverReturnsTrailingDotOrSpace()
    {
        var sanitized = PathSanitizer.Sanitize("Some. Title. . .");

        Assert.False(sanitized.EndsWith('.'));
        Assert.Equal(sanitized, sanitized.TrimEnd(' '));
    }

    [Fact]
    public void SamePath_IgnoresTrailingSeparators()
    {
        var dir = Path.Combine(Path.GetTempPath(), "j2j-same-path");

        Assert.True(PathSanitizer.SamePath(dir, dir + Path.DirectorySeparatorChar));
        Assert.False(PathSanitizer.SamePath(dir, Path.Combine(Path.GetTempPath(), "j2j-other")));
    }

    [Fact]
    public void SamePath_IsCaseInsensitiveOnlyOnWindows()
    {
        var lower = Path.Combine(Path.GetTempPath(), "j2j-same-path");
        var upper = Path.Combine(Path.GetTempPath(), "J2J-SAME-PATH");

        Assert.Equal(OperatingSystem.IsWindows(), PathSanitizer.SamePath(lower, upper));
    }

    [Fact]
    public void SamePath_ReturnsFalseForMissingValues()
    {
        Assert.False(PathSanitizer.SamePath(null, "/media"));
        Assert.False(PathSanitizer.SamePath("/media", " "));
    }
}
