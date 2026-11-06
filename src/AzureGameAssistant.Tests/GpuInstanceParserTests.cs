using AzureGameAssistant.Core.Gpu;

namespace AzureGameAssistant.Tests;

public sealed class GpuInstanceParserTests
{
    [Theory]
    [InlineData("pid_1234_luid_0x00000000_0x0000ABCD_phys_0_eng_0_engtype_3D", 1234u, 0x000000000000ABCDul, 0, GpuEngineKind.ThreeD)]
    [InlineData("pid_42_luid_0x00000001_0x12345678_phys_1_eng_0_engtype_Copy", 42u, 0x0000000112345678ul, 1, GpuEngineKind.Copy)]
    [InlineData("pid_77_luid_0x00000000_0x00FF00FF_phys_0_eng_0_engtype_VideoDecode", 77u, 0x0000000000FF00FFul, 0, GpuEngineKind.Other)]
    public void TryParse_AcceptsEngineInstances(string instance, uint pid, ulong luid, int phys, GpuEngineKind engine)
    {
        Assert.True(GpuInstanceParser.TryParse(instance, out GpuInstanceInfo info));
        Assert.Equal(pid, info.Pid);
        Assert.Equal(luid, info.Luid);
        Assert.Equal(phys, info.Phys);
        Assert.Equal(engine, info.Engine);
    }

    [Theory]
    [InlineData("")]
    [InlineData("pid_0_luid_0x00000000_0x00000000_phys_0_eng_0_engtype_3D_total")]
    [InlineData("total")]
    [InlineData("pid_abc_luid_0x00000000_0x00000001_phys_0_eng_0_engtype_3D")]
    [InlineData("pid_1_phys_0_eng_0_engtype_3D")]
    [InlineData("pid_1_luid_zz_0x1_phys_0_eng_0_engtype_3D")]
    [InlineData("pid_1_luid_0x1_zz_phys_0_eng_0_engtype_3D")]
    public void TryParse_RejectsMalformedOrTotalInstances(string instance)
    {
        Assert.False(GpuInstanceParser.TryParse(instance, out _));
    }
}
