using FanaBridge.Display.Rules;
using FanaBridge.UI.Display.Shared;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// The v9 dense-grid column projections (<see cref="TriggerTableModel"/>): the Timeout
    /// wording for every hold kind and the State wording for the live-status × enabled combos.
    /// Pure — no WPF, no config — so the workbench columns read the same everywhere.
    /// </summary>
    public class TriggerTableModelTests
    {
        [Theory]
        [InlineData(HoldKind.WhileActive, 5000, "While active")]
        [InlineData(HoldKind.Indefinite, 5000, "Until replaced")]
        [InlineData(HoldKind.ForDuration, 5000, "5 s")]
        [InlineData(HoldKind.ForDuration, 2500, "2.5 s")]
        [InlineData(HoldKind.Unknown, 5000, "While active")]   // unset → the level default look
        public void TimeoutText_MapsEveryHoldKind(HoldKind kind, int durationMs, string expected)
            => Assert.Equal(expected, TriggerTableModel.TimeoutText(kind, durationMs));

        [Theory]
        [InlineData(RuleStatus.OnScreen, true, "on screen")]
        [InlineData(RuleStatus.Waiting, true, "waiting")]
        [InlineData(RuleStatus.Armed, true, "")]
        [InlineData(RuleStatus.Unavailable, true, "n/a on this wheel")]
        [InlineData(RuleStatus.Ineligible, true, "")]
        public void StateText_MapsLiveStatus_WhenEnabled(RuleStatus status, bool enabled, string expected)
            => Assert.Equal(expected, TriggerTableModel.StateText(status, enabled));

        [Theory]
        [InlineData(RuleStatus.Armed)]
        [InlineData(RuleStatus.Waiting)]
        [InlineData(RuleStatus.OnScreen)]
        public void StateText_Disabled_AlwaysReadsOff(RuleStatus status)
            => Assert.Equal("off", TriggerTableModel.StateText(status, enabled: false));
    }
}
