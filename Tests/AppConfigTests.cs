using System;
using System.Collections.Generic;
using Xunit;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class AppConfigTests
{
    private static Dictionary<string, string> Parse(params string[] lines)
    {
        return AppConfig.ParseIniLines(lines);
    }

    [Fact]
    public void ParseIniLines_ReadsKeyValuePairs()
    {
        var v = Parse("DisableSearch=true", "DisableAltPan=false");
        Assert.Equal("true", v["DisableSearch"]);
        Assert.Equal("false", v["DisableAltPan"]);
    }

    [Fact]
    public void ParseIniLines_SkipsCommentsBlanksAndSectionHeaders()
    {
        var v = Parse(
            "[CanvasWindowComposer]",
            "",
            "   ",
            "; DisableSearch=true",
            "# DisableAltPan=true",
            "DisableGreedyDraw=true");

        Assert.Single(v);
        Assert.True(v.ContainsKey("DisableGreedyDraw"));
    }

    [Fact]
    public void ParseIniLines_TrimsSurroundingWhitespace()
    {
        var v = Parse("   DisableSearch   =   true   ");
        Assert.Equal("true", v["DisableSearch"]);
    }

    [Fact]
    public void ParseIniLines_KeysAreCaseInsensitive()
    {
        var v = Parse("disablesearch=true");
        Assert.Equal("true", v["DisableSearch"]);
    }

    [Fact]
    public void ParseIniLines_IgnoresMalformedLines()
    {
        var v = Parse("no equals sign here", "=novalue", "Good=1");
        Assert.Single(v);
        Assert.Equal("1", v["Good"]);
    }

    [Fact]
    public void ParseIniLines_LastDuplicateWins()
    {
        var v = Parse("DisableSearch=false", "DisableSearch=true");
        Assert.Equal("true", v["DisableSearch"]);
    }

    [Fact]
    public void ParseIniLines_KeepsValueContainingEquals()
    {
        var v = Parse("Key=a=b");
        Assert.Equal("a=b", v["Key"]);
    }

    [Fact]
    public void GetBool_MissingKey_ReturnsDefault()
    {
        var v = Parse("Other=true");
        Assert.True(AppConfig.GetBool(v, "Missing", defaultValue: true));
        Assert.False(AppConfig.GetBool(v, "Missing", defaultValue: false));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("yes", false)]
    [InlineData("1", false)]
    [InlineData("", false)]
    public void GetBool_OnlyLiteralTrueIsTrue(string raw, bool expected)
    {
        var v = Parse($"Flag={raw}");
        // A commented-out or absent flag falls back to the default; a present
        // one is true only when it literally says "true". Anything else being
        // treated as false is the documented behaviour, not an accident.
        Assert.Equal(expected, AppConfig.GetBool(v, "Flag", defaultValue: false));
    }

    [Fact]
    public void ApplyValues_UsesDocumentedDefaultsForAbsentKeys()
    {
        var config = new AppConfig();
        config.ApplyValues(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        Assert.False(config.DisableSearch);
        Assert.False(config.DisableAltPan);
        Assert.True(config.DisableGreedyDraw);
        Assert.True(config.ShowScreenFixedWindowsDuringPan);
        Assert.False(config.DisableMouseCurve);
        Assert.False(config.DisableZoomHotkey);
    }

    [Fact]
    public void ApplyValues_OverridesEveryFlag()
    {
        var config = new AppConfig();
        config.ApplyValues(Parse(
            "DisableSearch=true",
            "DisableAltPan=true",
            "DisableGreedyDraw=false",
            "ShowScreenFixedWindowsDuringPan=false",
            "DisableMouseCurve=true",
            "DisableZoomHotkey=true"));

        Assert.True(config.DisableSearch);
        Assert.True(config.DisableAltPan);
        Assert.False(config.DisableGreedyDraw);
        Assert.False(config.ShowScreenFixedWindowsDuringPan);
        Assert.True(config.DisableMouseCurve);
        Assert.True(config.DisableZoomHotkey);
    }

    // ==================== GRID COLUMNS (non-boolean setting) ====================

    [Fact]
    public void GetInt_ReadsAValue()
    {
        Assert.Equal(5, AppConfig.GetInt(Parse("GridColumns=5"), "GridColumns", 3, 1, 32));
    }

    [Fact]
    public void GetInt_MissingKey_ReturnsDefault()
    {
        Assert.Equal(3, AppConfig.GetInt(Parse("Other=9"), "GridColumns", 3, 1, 32));
    }

    [Theory]
    [InlineData("three")]
    [InlineData("")]
    [InlineData("3.5")]
    [InlineData("0x10")]
    [InlineData("999999999999999999999")]
    public void GetInt_Unparseable_FallsBackToDefaultRatherThanThrowing(string raw)
    {
        // A typo in config.ini must not take the app down on a live reload.
        Assert.Equal(3, AppConfig.GetInt(Parse($"GridColumns={raw}"), "GridColumns", 3, 1, 32));
    }

    [Fact]
    public void GetInt_ClampsOutOfRangeValues()
    {
        Assert.Equal(1, AppConfig.GetInt(Parse("GridColumns=0"), "GridColumns", 3, 1, 32));
        Assert.Equal(1, AppConfig.GetInt(Parse("GridColumns=-7"), "GridColumns", 3, 1, 32));
        Assert.Equal(32, AppConfig.GetInt(Parse("GridColumns=3000"), "GridColumns", 3, 1, 32));
    }

    [Fact]
    public void GetInt_ToleratesSurroundingWhitespace()
    {
        Assert.Equal(4, AppConfig.GetInt(Parse("GridColumns =  4  "), "GridColumns", 3, 1, 32));
    }

    [Fact]
    public void ApplyValues_GridColumnsDefaultsToThree()
    {
        var config = new AppConfig();
        config.ApplyValues(Parse("DisableSearch=true"));
        Assert.Equal(3, config.GridColumns);
    }

    [Fact]
    public void ApplyValues_GridColumnsIsClampedToTheArrangerBounds()
    {
        var config = new AppConfig();

        config.ApplyValues(Parse("GridColumns=6"));
        Assert.Equal(6, config.GridColumns);

        config.ApplyValues(Parse("GridColumns=99999"));
        Assert.Equal(GridArranger.MaxColumns, config.GridColumns);

        config.ApplyValues(Parse("GridColumns=0"));
        Assert.Equal(GridArranger.MinColumns, config.GridColumns);
    }
}
