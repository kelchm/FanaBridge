using SimHub.Plugins.OutputPlugins.Dash.GLCDTemplating;
using SimHub.Plugins.OutputPlugins.Dash.TemplatingCommon;

namespace FanaBridge.Shared
{
    /// <summary>
    /// <see cref="INCalcEngine"/> backed by SimHub's NCalc engine.
    /// </summary>
    public class SimHubNCalcEngine : INCalcEngine
    {
        private readonly NCalcEngineBase _engine;

        public SimHubNCalcEngine(NCalcEngineBase engine)
        {
            _engine = engine;
        }

        public object Evaluate(string expression)
        {
            if (_engine == null || string.IsNullOrEmpty(expression)) return null;
            try
            {
                var expr = new ExpressionValue { Expression = expression };
                return _engine.ParseValue(expr);
            }
            catch { return null; }
        }
    }
}
