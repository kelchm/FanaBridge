namespace FanaBridge.UI.Display
{
    /// <summary>
    /// Which prioritized rule list a <see cref="DisplayTriggersEditModel"/> edits.
    /// ITM wheels author page/cycle targets on <c>config.Itm.Rules</c>; the legacy
    /// 7-segment surface (basic wheels, and ITM Page 6 content) authors
    /// <see cref="FanaBridge.Display.Rules.TargetKind.Screen"/> targets on
    /// <c>config.Legacy.Rules</c>.
    /// </summary>
    internal enum TriggerRuleSet
    {
        Itm,
        Legacy,
    }
}
