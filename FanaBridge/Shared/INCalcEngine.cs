namespace FanaBridge.Shared
{
    /// <summary>
    /// Abstraction for evaluating NCalc expressions.
    /// Implementations wrap SimHub's NCalc integration; tests inject mocks.
    /// </summary>
    public interface INCalcEngine
    {
        /// <summary>
        /// Evaluates an NCalc expression and returns the result.
        /// Returns null on error.
        /// </summary>
        object Evaluate(string expression);
    }
}
