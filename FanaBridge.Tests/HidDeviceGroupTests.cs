using FanaBridge.Devices;
using Xunit;

namespace FanaBridge.Tests
{
    /// <summary>
    /// Covers the pure physical-device-key derivation (<see cref="HidDeviceGroup.DeriveDeviceKey"/>
    /// and <see cref="HidDeviceGroup.InstanceSegment"/>). The key must distinguish two
    /// same-PID devices on different ports (the multi-base case) while a single device's
    /// collection interfaces resolve to one key, and degrade gracefully to VID:PID when a
    /// path is unparseable. The cross-port distinctness ITSELF is hardware-validated
    /// separately; these tests pin the parsing contract the validation depends on.
    /// </summary>
    public class HidDeviceGroupTests
    {
        // Canonical Windows HID collection path: \\?\HID#<hwid&ColXX>#<instance>#{guid}
        private static string Path(string col, string instance)
            => $@"\\?\HID#VID_0EB7&PID_0005&{col}#{instance}#{{4d1e55b2-f16f-11cf-88cb-001111000030}}";

        // ── InstanceSegment ──────────────────────────────────────────────

        [Fact]
        public void InstanceSegment_ReturnsMiddleSegment_Lowercased()
            => Assert.Equal("8&1ec7e9f5&0&0002",
                HidDeviceGroup.InstanceSegment(Path("Col03", "8&1EC7E9F5&0&0002")));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(@"\\?\HID#VID_0EB7&PID_0005&Col03")]            // too few segments
        [InlineData("not-a-device-path")]
        public void InstanceSegment_ReturnsNull_WhenNotParseable(string path)
            => Assert.Null(HidDeviceGroup.InstanceSegment(path));

        // ── DeriveDeviceKey ──────────────────────────────────────────────

        [Fact]
        public void DeriveDeviceKey_IncludesInstance_WhenParseable()
            => Assert.Equal("0EB7:0005:8&1ec7e9f5&0&0002",
                HidDeviceGroup.DeriveDeviceKey(0x0EB7, 0x0005,
                    new[] { Path("Col03", "8&1EC7E9F5&0&0002") }));

        [Fact]
        public void DeriveDeviceKey_SamePidDifferentPort_ProducesDistinctKeys()
        {
            // Two physically distinct same-PID bases — the multi-base case the key exists for.
            var a = HidDeviceGroup.DeriveDeviceKey(0x0EB7, 0x0005, new[] { Path("Col03", "8&1ec7e9f5&0&0002") });
            var b = HidDeviceGroup.DeriveDeviceKey(0x0EB7, 0x0005, new[] { Path("Col03", "8&2fd80abc&0&0002") });
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void DeriveDeviceKey_DifferentPid_ProducesDistinctKeys()
        {
            // base + SRM (distinct PIDs) — distinguished even without instance parsing.
            var baseKey = HidDeviceGroup.DeriveDeviceKey(0x0EB7, 0x0020, new[] { Path("Col03", "8&1ec7e9f5&0&0002") });
            var srmKey = HidDeviceGroup.DeriveDeviceKey(0x1DD2, 0x2011, new[] { Path("Col01", "8&1ec7e9f5&0&0002") });
            Assert.NotEqual(baseKey, srmKey);
        }

        [Fact]
        public void DeriveDeviceKey_PicksFirstParseableInstance_SkippingNulls()
            => Assert.Equal("0EB7:0005:8&1ec7e9f5&0&0002",
                HidDeviceGroup.DeriveDeviceKey(0x0EB7, 0x0005,
                    new[] { null, "garbage", Path("Col01", "8&1EC7E9F5&0&0002") }));

        [Fact]
        public void DeriveDeviceKey_FallsBackToVidPid_WhenNoInstanceParseable()
            => Assert.Equal("0EB7:0005",
                HidDeviceGroup.DeriveDeviceKey(0x0EB7, 0x0005, new[] { (string)null, "garbage" }));

        [Fact]
        public void DeriveDeviceKey_FallsBackToVidPid_WhenNoPaths()
            => Assert.Equal("0EB7:0005", HidDeviceGroup.DeriveDeviceKey(0x0EB7, 0x0005, null));
    }
}
