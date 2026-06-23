using System;

namespace FFTTools {

	public class LCRWideOptions {
		public double CenterRemove = 1.0;
		public double CenterGain = 1.0;
		public double WideGain = 0.35;
		public double WideRemove = 0.5;
		public double WideExponent = 2.5;
		public double WideLowCutHz = 250.0;
		public double WideSmoothMs = 80.0;
		public double WidePhaseWeight = 1.0;
		public bool PreserveStereoDownmix = true;
		public double LCR7PanSharpness = 1.0;
		public double LCR7CLCRPosition = 0.5;
		public double XYRearGain = 1.0;
		public double XYRearExponent = 1.5;
		public double XYSharpness = 1.5;
		public double XYFrontBias = 0.0;
		public bool XY7Mode = false;
		public bool DryCenterResidualMode = false;
	}

    internal static class WindowMath {
        public static double[] BuildPowerSineWindow(int length, double exponent) {
            if (length <= 0) throw new ArgumentOutOfRangeException("length");
            double[] window = new double[length];
            double angularStep = Math.PI / (double)length;
            for (int index = 0; index < length; index++) {
                double s = Math.Sin((index + 0.5) * angularStep);
                double baseValue = s * s;
                window[index] = exponent == 1.0 ? baseValue : Math.Pow(baseValue, exponent);
            }
            return window;
        }
    }

}
