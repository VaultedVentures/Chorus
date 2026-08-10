using Chorus.Core;

namespace Chorus.Core.Tests;

public class MicDeviceResolverTests
{
    private static readonly string[] Devices =
    {
        "Microphone Array (Realtek Audio)",
        "External Mic (USB Audio Device)",
        "Line In (Synaptics)",
    };

    [Fact]
    public void Empty_Spec_Resolves_To_Default()
    {
        Assert.Equal(0, MicDeviceResolver.ResolveIndex("", Devices));
        Assert.Equal(0, MicDeviceResolver.ResolveIndex("   ", Devices));
        Assert.Equal(0, MicDeviceResolver.ResolveIndex(null!, Devices));
    }

    [Fact]
    public void Numeric_Spec_Resolves_To_Index()
    {
        Assert.Equal(0, MicDeviceResolver.ResolveIndex("0", Devices));
        Assert.Equal(2, MicDeviceResolver.ResolveIndex("2", Devices));
    }

    [Fact]
    public void Out_Of_Range_Index_Clamps_To_Default()
    {
        Assert.Equal(0, MicDeviceResolver.ResolveIndex("99", Devices));
        Assert.Equal(0, MicDeviceResolver.ResolveIndex("-1", Devices));
    }

    [Fact]
    public void Name_Substring_Matches_Case_Insensitively()
    {
        Assert.Equal(1, MicDeviceResolver.ResolveIndex("usb", Devices));
        Assert.Equal(1, MicDeviceResolver.ResolveIndex("USB Audio", Devices));
        Assert.Equal(0, MicDeviceResolver.ResolveIndex("REALTEK", Devices));
    }

    [Fact]
    public void No_Name_Match_Falls_Back_To_Default()
    {
        Assert.Equal(0, MicDeviceResolver.ResolveIndex("nonexistent mic", Devices));
    }

    [Fact]
    public void Empty_Device_List_Returns_Minus_One()
    {
        Assert.Equal(-1, MicDeviceResolver.ResolveIndex("", Array.Empty<string>()));
        Assert.Equal(-1, MicDeviceResolver.ResolveIndex("0", Array.Empty<string>()));
    }

    [Fact]
    public void Describe_Names_Resolved_Device()
    {
        var d = MicDeviceResolver.Describe("usb", Devices, 1);
        Assert.Contains("External Mic", d);
        Assert.Contains("device 1", d);

        var def = MicDeviceResolver.Describe("", Devices, 0);
        Assert.Contains("default input device", def);
    }
}
