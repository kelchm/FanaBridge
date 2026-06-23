using FanaBridge.Protocol;
using FanaBridge.Protocol.Schema;
using Xunit;

namespace FanaBridge.Tests
{
    /// <summary>
    /// Locks the schema definitions to the wire offsets and to the live decode
    /// constants they now feed. If a definition offset drifts, these fail.
    /// </summary>
    public class SchemaDriftTests
    {
        [Fact]
        public void SystemReportOffsets_AreTheWireOffsets_AndDriveIdentity()
        {
            Assert.Equal(0x02, SystemReport.BaseType.Offset);
            Assert.Equal(0x18, SystemReport.WheelCode.Offset);
            Assert.Equal(0x1F, SystemReport.Module.Offset);

            Assert.Equal(SystemReport.BaseType.Offset, FanatecIdentity.OffBaseType);
            Assert.Equal(SystemReport.WheelCode.Offset, FanatecIdentity.OffWireCode);
            Assert.Equal(SystemReport.Module.Offset, FanatecIdentity.OffModule);
        }

        [Fact]
        public void ButtonModuleEncoderOffsets_MatchControllerConstants()
        {
            Assert.Equal(18, ButtonModuleTuning.ReadOffset);
            Assert.Equal(19, ButtonModuleTuning.WriteOffset);

            Assert.Equal(ButtonModuleTuning.ReadOffset, FanatecTuningController.READ_ENCODER_MODE_OFFSET);
            Assert.Equal(ButtonModuleTuning.WriteOffset, FanatecTuningController.WRITE_ENCODER_MODE_OFFSET);
        }
    }
}
