namespace FanaBridge.Shared.Conditions
{
    /// <summary>
    /// Condition that is always active. Used for Screen layers
    /// that should always be available in the cycle.
    /// </summary>
    public class AlwaysActive : ActivationCondition
    {
        public override string Type { get { return "AlwaysActive"; } }

        public override bool Evaluate(IPropertyProvider props, ActivationState state, long nowMs)
        {
            return true;
        }
    }
}
