namespace RocketWorld.Sim
{
    /// <summary>RocketSim's LinearPieceCurve: piecewise linear, clamped at both ends; empty curve returns the default.</summary>
    public sealed class LinearCurve
    {
        readonly float[] _in, _out;
        public LinearCurve(params float[] pairs)
        {
            int n = pairs.Length / 2; _in = new float[n]; _out = new float[n];
            for (int i = 0; i < n; i++) { _in[i] = pairs[2 * i]; _out[i] = pairs[2 * i + 1]; }
        }
        public float GetOutput(float input, float defaultOutput = 1f)
        {
            if (_in.Length == 0) return defaultOutput;
            if (input <= _in[0]) return _out[0];
            for (int i = 1; i < _in.Length; i++)
                if (_in[i] > input)
                {
                    float range = _in[i] - _in[i - 1], diff = _out[i] - _out[i - 1];
                    float f = (input - _in[i - 1]) / range;
                    return _out[i - 1] + diff * f;
                }
            return _out[_out.Length - 1];
        }
    }
}
