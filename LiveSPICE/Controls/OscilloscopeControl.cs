﻿using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Numerics;
using SyMath;

namespace LiveSPICE
{
    public class OscilloscopeControl : Control, INotifyPropertyChanged
    {
        protected const double MinFrequency = 20.0;
        protected const double MaxPeriod = 1.0 / MinFrequency;

        static OscilloscopeControl() { DefaultStyleKeyProperty.OverrideMetadata(typeof(OscilloscopeControl), new FrameworkPropertyMetadata(typeof(OscilloscopeControl))); }

        private Circuit.Quantity sampleRate;
        public Circuit.Quantity SampleRate { get { return sampleRate; } }

        private Circuit.Quantity a4 = new Circuit.Quantity(440, Circuit.Units.Hz);
        public Circuit.Quantity A4
        {
            get { return a4; }
            set
            {
                a4.Set(value);
                InvalidateVisual();
                NotifyChanged("A4");
            }
        }

        protected bool showNotes = true;
        public bool ShowNotes 
        {
            get { return showNotes; } 
            set 
            { 
                showNotes = value; 
                InvalidateVisual(); 
                NotifyChanged("ShowNotes"); 
            } 
        }
        
        protected double zoom = 10.0f / 440.0f;
        public double Zoom 
        { 
            get { return zoom; } 
            set 
            { 
                zoom = Math.Min(Math.Max(value, 16.0 / (double)sampleRate), 2 * MaxPeriod); 
                InvalidateVisual(); 
                NotifyChanged("Zoom"); 
            }
        }

        public Pen GridPen = new Pen(Brushes.Gray, 0.25);
        public Pen AxisPen = new Pen(Brushes.Gray, 0.5);
        public Pen TracePen = new Pen(Brushes.White, 0.5);

        /// <summary>
        /// Properties and methods associated with a particular signal.
        /// </summary>
        public class Signal : IEnumerable<double>
        {
            private List<double> samples = new List<double>();

            // Display parameters.
            private Pen pen = new Pen(Brushes.White, 1.0);
            public Pen Pen { get { return pen; } set { pen = value; } }

            private object tag;
            public object Tag { get { return tag; } set { tag = value; } }

            // Process new samples for this signal.
            public void AddSamples(long Clock, double[] Samples, int Truncate)
            {
                samples.AddRange(Samples);
                if (samples.Count > Truncate)
                {
                    int remove = samples.Count - Truncate;
                    samples.RemoveRange(0, remove);
                }

                clock = Clock;
            }

            private long clock = 0;
            public long Clock { get { return clock; } }

            public void Clear() { samples.Clear(); clock = 0; }

            public int Count { get { return samples.Count; } }
            public double this[int i] 
            { 
                get 
                {
                    if (0 <= i && i < samples.Count)
                        return samples[(int)i];
                    else
                        return double.NaN;
                } 
            }

            // IEnumerable<double> interface
            IEnumerator<double> IEnumerable<double>.GetEnumerator() { return samples.GetEnumerator(); }
            IEnumerator IEnumerable.GetEnumerator() { return samples.GetEnumerator(); }
        }

        protected ConcurrentDictionary<SyMath.Expression, Signal> signals = new ConcurrentDictionary<SyMath.Expression, Signal>();
        public ConcurrentDictionary<SyMath.Expression, Signal> Signals { get { return signals; } }
                
        private SyMath.Expression selected;
        public SyMath.Expression SelectedSignal 
        {
            get 
            {
                if (signals.Count > 0 && (selected == null || !signals.ContainsKey(selected)))
                    selected = signals.First().Key;
                return selected; 
            }
            set { selected = value; NotifyChanged("SelectedSignal"); } 
        }

        protected long clock = 0;
        protected double Vmax;
        protected Point? tracePoint;
        
        public OscilloscopeControl()
        {
            Background = Brushes.DimGray;
            Cursor = Cursors.Cross;
            FontFamily = new FontFamily("Courier New");

            CommandBindings.Add(new CommandBinding(NavigationCommands.Zoom, (o, e) => Zoom *= 0.5));
            CommandBindings.Add(new CommandBinding(NavigationCommands.DecreaseZoom, (o, e) => Zoom *= 2.0));

            MouseWheel += (o, e) => { Zoom /= (float)Math.Pow(2.0f, (float)e.Delta / (120.0f * 4.0f)); InvalidateVisual(); };
            MouseMove += (o, e) => { tracePoint = e.GetPosition(this); InvalidateVisual(); };
            MouseLeave += (o, e) => { tracePoint = null; InvalidateVisual(); };
            MouseDown += (o, e) => { Focus(); InvalidateVisual(); };
        }
        
        public void Clear()
        {
            signals.Clear();
            InvalidateVisual();
        }

        public void ProcessSignals(int SampleCount, IEnumerable<KeyValuePair<SyMath.Expression, double[]>> Signals, Circuit.Quantity Rate)
        {
            clock += SampleCount;
            sampleRate = Rate;

            int truncate = (int)(4 * (double)sampleRate * MaxPeriod);

            // Add signal data.
            foreach (KeyValuePair<SyMath.Expression, double[]> i in Signals)
            {
                Signal S;
                if (signals.TryGetValue(i.Key.ToString(), out S))
                    lock (S) S.AddSamples(clock, i.Value, truncate);
            }

            // Remove the signals that we didn't get data for.
            foreach (KeyValuePair<SyMath.Expression, Signal> i in signals.Where(j => j.Value.Clock < clock))
                lock(i.Value) i.Value.Clear();

            Dispatcher.InvokeAsync(() => InvalidateVisual(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        protected override void OnRender(DrawingContext DC)
        {
            DC.DrawRectangle(BorderBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
            Rect bounds = new Rect(
                BorderThickness.Left, 
                BorderThickness.Top, 
                ActualWidth - (BorderThickness.Right + BorderThickness.Left), 
                ActualHeight - (BorderThickness.Top + BorderThickness.Bottom));
            DC.PushClip(new RectangleGeometry(bounds));
            DC.DrawRectangle(Background, null, bounds);

            DrawTimeAxis(DC, bounds);

            Signal stats = null;
            if (SelectedSignal == null || !signals.TryGetValue(SelectedSignal, out stats))
                stats = signals.Values.FirstOrDefault(i => { lock (i) return i.Count > 0; });

            double peak = 0.0;
            double rms = 0.0;
            double f0 = 0.0;
            int align = 0;
            long sync = 0;

            if (stats != null)
            {
                lock (stats)
                {
                    // Remembe the clock for when we analyzed the signal to keep the signals in sync even if new data gets added in the background.
                    sync = stats.Clock;

                    // Compute statistics of the clock signal.
                    align = stats.Count;
                    peak = stats.Max(i => Math.Abs(i), 0.0);
                    rms = Math.Sqrt(stats.Sum(i => i * i) / stats.Count);

                    int Decimate = 1 << (int)Math.Floor(Math.Log((double)sampleRate / 24000, 2));
                    int BlockSize = 8192;
                    if (stats.Count >= BlockSize)
                    {
                        double[] data = stats.Skip(stats.Count - BlockSize).ToArray();

                        // Estimate the fundamental frequency of the signal.
                        double phase;
                        double f = EstimateFrequency(data, Decimate, out phase);

                        // Convert phase from (-pi, pi] to (0, 1]
                        phase = ((phase + Math.PI) / (2 * Math.PI));

                        // Shift all the signals by the phase in samples to align the signal between frames.
                        if (f > 1.0)
                            align -= (int)Math.Round(phase * BlockSize / f);

                        // Compute fundamental frequency in Hz.
                        f0 = (double)sampleRate * f / BlockSize;
                    }
                }
            }
            else if (signals.Count > 0)
            {
                align = signals.Max(i => { lock (i.Value) return i.Value.Count; });

                foreach (Signal i in signals.Values)
                    lock(i) peak = Math.Max(peak, i.Max(j => Math.Abs(j), 0.0));
            }
                                
            // Compute the target min/max
            double window = Math.Pow(2.0, Math.Ceiling(Math.Log(peak * 1.1 + 1e-9, 2.0)));
            // Animate transition from current min/max to target min/max.
            if (double.IsNaN(Vmax))
            {
                Vmax = window;
            }
            else
            {
                // Lerp in log domain to cover huge distances quickly.
                Vmax = System.Math.Log(Math.Max(Vmax, window));
                window = System.Math.Log(window);
                Vmax = System.Math.Exp(Vmax + (window - Vmax) * 0.1);
            }

            Vmax = Math.Max(Vmax, 1e-2);

            DrawSignalAxis(DC, bounds);

            foreach (Signal i in signals.Values.Except(stats))
                lock (i) DrawSignal(DC, bounds, i, align - (int)(i.Clock - sync));
            
            // Draw the focus signal last.
            if (stats != null)
            {
                lock (stats) DrawSignal(DC, bounds, stats, align - (int)(stats.Clock - sync));
                DrawStatistics(DC, bounds, stats.Pen.Brush, peak, rms, f0);
            }

            if (tracePoint.HasValue)
                DrawTrace(DC, bounds, tracePoint.Value);

            DC.Pop();
        }

        protected void DrawTimeAxis(DrawingContext DC, Rect Bounds)
        {
            double y = (Bounds.Bottom + Bounds.Top) / 2;
            
            // Draw axis.
            DC.DrawLine(AxisPen, new Point(Bounds.Left, y), new Point(Bounds.Right, y));

            Typeface typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);

            double tmin = MapToTime(Bounds, 0);
            double dt = Partition(MapToTime(Bounds, 80) - MapToTime(Bounds, 0));
            for (double t = -dt; t > tmin; t -= dt)
            {
                double x = MapFromTime(Bounds, t);

                DC.DrawLine(GridPen, new Point(x, Bounds.Top), new Point(x, Bounds.Bottom));

                DC.DrawLine(AxisPen, new Point(x, y - 3), new Point(x, y + 3));

                FormattedText time = new FormattedText(
                    Circuit.Quantity.ToString(-t, Circuit.Units.s),
                    System.Globalization.CultureInfo.CurrentCulture,
                    System.Windows.FlowDirection.LeftToRight,
                    typeface, FontSize,
                    AxisPen.Brush);
                DC.DrawText(time, new Point(x - time.Width / 2, y - time.Height - 2));

                FormattedText freq = new FormattedText(
                    FrequencyToString(1.0 / Math.Abs(t)),
                    System.Globalization.CultureInfo.CurrentCulture,
                    System.Windows.FlowDirection.LeftToRight,
                    typeface, FontSize,
                    AxisPen.Brush);
                DC.DrawText(freq, new Point(x - freq.Width / 2, y + 3));
            }
        }

        protected void DrawSignalAxis(DrawingContext DC, Rect Bounds)
        {
            // Draw axis on the left.
            double x = Bounds.Right - 1;

            double dv = Partition(MapToSignal(Bounds, 0) - MapToSignal(Bounds, 50));

            int labels = (int)((2 * Vmax) / dv);
            labels = (labels / 2) * 2;

            Typeface typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);

            for (double v = -Vmax + (2 * Vmax - (labels * dv)) / 2; v < Vmax; v += dv)
            {
                double y = MapFromSignal(Bounds, v);
                if (y + 10 < (Bounds.Top + Bounds.Bottom) / 2 || 
                    y - 10 > (Bounds.Top + Bounds.Bottom) / 2)
                {
                    DC.DrawLine(GridPen, new Point(Bounds.Left, y), new Point(Bounds.Right, y));

                    DC.DrawLine(AxisPen, new Point(x - 3, y), new Point(x + 3, y));

                    FormattedText volts = new FormattedText(
                        Circuit.Quantity.ToString(v, Circuit.Units.V, "+G3"),
                        System.Globalization.CultureInfo.CurrentCulture,
                        System.Windows.FlowDirection.LeftToRight,
                        typeface, FontSize,
                        AxisPen.Brush);
                    DC.DrawText(volts, new Point(x - volts.Width - 3, y - volts.Height / 2));
                }
            }
        }

        protected void DrawSignal(DrawingContext DC, Rect Bounds, Signal S, int shift)
        {
            // Rate of pixels to sample.
            const double rate = 1.0;
            
            // How many pixels map to one sample.
            double margin = Bounds.Width / (double)(zoom * (double)sampleRate);
            List<Point> points = new List<Point>();
            for (double i = -margin; i <= Bounds.Right + margin; i += rate)
            {
                int s0 = MapToSample(Bounds, i, shift);
                int s1 = MapToSample(Bounds, i + rate, shift);

                if (s1 > s0)
                {
                    // Anti-aliasing.
                    double v = 0.0;
                    for (int j = s0; j < s1; ++j)
                        v += S[j];

                    if (!double.IsNaN(v))
                        points.Add(new Point(i, MapFromSignal(Bounds, v / (s1 - s0))));
                }
            }

            points = DouglasPeuckerReduction(points, 0.5);
            for (int i = 0; i + 1 < points.Count; ++i)
                DC.DrawLine(S.Pen, points[i], points[i + 1]);
        }

        protected void DrawTrace(DrawingContext DC, Rect Bounds, Point At)
        {
            DC.DrawLine(TracePen, new Point(Bounds.Left, At.Y), new Point(Bounds.Right, At.Y));
            DC.DrawLine(TracePen, new Point(At.X, Bounds.Top), new Point(At.X, Bounds.Bottom));

            Typeface typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);

            double t = MapToTime(Bounds, At.X);
            FormattedText time = new FormattedText(
                Circuit.Quantity.ToString(-t, Circuit.Units.s),
                System.Globalization.CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                typeface, FontSize,
                TracePen.Brush);
            DC.DrawText(time, new Point(At.X - time.Width - 2, (Bounds.Top + Bounds.Bottom) / 2 - time.Height - 2));
            FormattedText freq = new FormattedText(
                FrequencyToString(1.0 / Math.Abs(t)),
                System.Globalization.CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                typeface, FontSize,
                TracePen.Brush);
            DC.DrawText(freq, new Point(At.X - freq.Width - 2, (Bounds.Top + Bounds.Bottom) / 2 + 3));

            double v = MapToSignal(Bounds, At.Y);
            FormattedText volts = new FormattedText(
                Circuit.Quantity.ToString(v, Circuit.Units.V, "+G3"),
                System.Globalization.CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                typeface, FontSize,
                TracePen.Brush);
            DC.DrawText(volts, new Point(Bounds.Right - volts.Width - 4, At.Y));
        }

        protected void DrawStatistics(DrawingContext DC, Rect Bounds, Brush Brush, double Peak, double Rms, double Freq)
        {
            FormattedText stats = new FormattedText(
                String.Format(
                    "\u0192\u2080:   {0}\nPeak: {1}\nRms:  {2}", 
                    FrequencyToString(Freq),
                    Circuit.Quantity.ToString(Peak, Circuit.Units.V, "+G3"),
                    Circuit.Quantity.ToString(Rms, Circuit.Units.V, "+G3")),
                System.Globalization.CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize,
                Brush);
            DC.DrawText(stats, Bounds.TopLeft);
        }
        
        private double MapFromTime(Rect Bounds, double t) { return Bounds.Right + (t * Bounds.Width / zoom); }
        private double MapToTime(Rect Bounds, double x) { return (x - Bounds.Right) * zoom / Bounds.Width; }

        private int MapToSample(Rect Bounds, double x, int shift) { return (int)Math.Round(((x - Bounds.Right) * zoom * (double)sampleRate) / Bounds.Width) + shift; }

        private double MapToSignal(Rect Bounds, double y) { return Vmax - (y - Bounds.Top) * 2 * Vmax / Bounds.Height; }
        private double MapFromSignal(Rect Bounds, double v) { return Bounds.Top + ((Vmax - v) / (2 * Vmax) * Bounds.Height); }

        private string FrequencyToString(double f)
        {
            if (ShowNotes)
                return FrequencyToNote(f, (double)A4);
            else
                return Circuit.Quantity.ToString(f, Circuit.Units.Hz);
        }

        private static double Partition(double P)
        {
	        double[] Partitions = { 10.0, 4.0, 2.0 };

	        double p = Math.Pow(10.0, Math.Ceiling(Math.Log10(P)));
	        foreach (double i in Partitions)
		        if (p / i > P)
			        return p / i;
	        return p;
        }

        private static string[] Notes = { "C", "C\u266f", "D", "D\u266f", "E", "F", "F\u266f", "G", "G\u266f", "A", "A\u266f", "B" };
        
        private static string FrequencyToNote(double f, double A4)
        {
            // Halfsteps above C0
            double halfsteps = (Math.Log(f / A4, 2.0) + 5.0) * 12.0 - 3.0;
            if (halfsteps < 0 || double.IsNaN(halfsteps) || double.IsInfinity(halfsteps))
                return "";

            int note = (int)Math.Round(halfsteps) % 12;
            int octave = (int)Math.Round(halfsteps) / 12;
            int cents = (int)Math.Round((halfsteps - Math.Round(halfsteps)) * 100);

            StringBuilder sb = new StringBuilder(Notes[note]);
            sb.Append(IntToSubscript(octave));
            sb.Append(' ');
            if (cents >= 0)
                sb.Append('+');
            sb.Append(cents);
            sb.Append('\u00A2');
            return sb.ToString();
        }

        private static string IntToSubscript(int x)
        {
            string chars = x.ToString();

            StringBuilder ret = new StringBuilder();
            foreach (char i in chars)
            {
                if (i == '-')
                    ret.Append((char)0x208B);
                else
                    ret.Append((char)(0x2080 + i - '0'));
            }
            return ret.ToString();
        }
        
        // INotifyPropertyChanged interface.
        private void NotifyChanged(string p)
        {
            if (PropertyChanged != null)
                PropertyChanged(this, new PropertyChangedEventArgs(p));
        }
        public event PropertyChangedEventHandler PropertyChanged;

        private static double Hann(int i, int N) { return 0.5 * (1.0 - Math.Cos((2.0 * 3.14159265 * i) / (N - 1))); }

        // Fit parabola to 3 bins and find the maximum.
        private static Complex LogParabolaMax(Complex A, Complex B, Complex C, out double x)
        {
            double a = A.Magnitude;
            double b = B.Magnitude;
            double c = C.Magnitude;

            if (b > a && b > c)
            {
                // Parabola fitting is more accurate in log magnitude.
                a = Math.Log(a);
                b = Math.Log(b);
                c = Math.Log(c);

                // Maximum location.
                x = (a - c) / (2.0 * (a - 2.0 * b + c));

                // Maximum value.
                return B - x * (A - C) / 4.0;
            }
            else
            {
                x = 0.0;
                return B;
            }
        }

        private static Complex[] DecimateSignal(double[] Block, int Decimate)
        {
            int N = Block.Length / Decimate;
            Complex[] data = new Complex[N];

            // Decimate input audio with low pass filter.
            for (int i = 0; i < N; ++i)
            {
                double v = 0.0;
                for (int j = 0; j < Decimate; ++j)
                    v += Block[i * Decimate + j];
                data[i] = new Complex(v * Hann(i, N), 0.0);
            }
            return data;
        }

        private static double EstimateFrequency(double[] Samples, int Decimate, out double Phase)
        {
            Complex[] data = DecimateSignal(Samples, Decimate);
            int N = data.Length;
            MathNet.Numerics.IntegralTransforms.Transform.FourierForward(data);

            double f = 0.0;
            double max = 0.0;
            Phase = 0.0;

            // Find largest frequency in FFT.
            for (int i = 1; i < N / 2 - 1; ++i)
            {
                double x;
                Complex m = LogParabolaMax(data[i - 1], data[i], data[i + 1], out x);

                if (m.Magnitude > max)
                {
                    max = m.Magnitude;
                    f = i + x;
                    Phase = m.Phase;
                }
            }

            // Check if this is a harmonic of another frequency (the fundamental frequency).
            double f0 = f;
            for (int h = 2; h < 5; ++h)
            {
                int i = (int)Math.Round(f / h);
                if (i >= 1)
                {
                    double x;
                    Complex m = LogParabolaMax(data[i - 1], data[i], data[i + 1], out x);

                    if (m.Magnitude * 5.0 > max)
                    {
                        f0 = f / h;
                        Phase = m.Phase;
                    }
                }
            }

            return f0;
        }

        // http://www.codeproject.com/Articles/18936/A-C-Implementation-of-Douglas-Peucker-Line-Approxi
        private static List<Point> DouglasPeuckerReduction(List<Point> Points, Double Tolerance)
        {
            if (Points == null || Points.Count < 3)
                return Points;

            Int32 firstPoint = 0;
            Int32 lastPoint = Points.Count - 1;
            List<Int32> pointIndexsToKeep = new List<Int32>();

            //Add the first and last index to the keepers
            pointIndexsToKeep.Add(firstPoint);
            pointIndexsToKeep.Add(lastPoint);

            //The first and the last point cannot be the same
            while (Points[firstPoint].Equals(Points[lastPoint]))
            {
                lastPoint--;
            }

            DouglasPeuckerReduction(Points, firstPoint, lastPoint,
            Tolerance, ref pointIndexsToKeep);

            List<Point> returnPoints = new List<Point>();
            pointIndexsToKeep.Sort();
            foreach (Int32 index in pointIndexsToKeep)
            {
                returnPoints.Add(Points[index]);
            }

            return returnPoints;
        }

        /// <span class="code-SummaryComment"><summary></span>
        /// Douglases the peucker reduction.
        /// <span class="code-SummaryComment"></summary></span>
        /// <span class="code-SummaryComment"><param name="points">The points.</param></span>
        /// <span class="code-SummaryComment"><param name="firstPoint">The first point.</param></span>
        /// <span class="code-SummaryComment"><param name="lastPoint">The last point.</param></span>
        /// <span class="code-SummaryComment"><param name="tolerance">The tolerance.</param></span>
        /// <span class="code-SummaryComment"><param name="pointIndexsToKeep">The point index to keep.</param></span>
        private static void DouglasPeuckerReduction(List<Point>
            points, Int32 firstPoint, Int32 lastPoint, Double tolerance,
            ref List<Int32> pointIndexsToKeep)
        {
            Double maxDistance = 0;
            Int32 indexFarthest = 0;

            for (Int32 index = firstPoint; index < lastPoint; index++)
            {
                Double distance = PerpendicularDistance
                    (points[firstPoint], points[lastPoint], points[index]);
                if (distance > maxDistance)
                {
                    maxDistance = distance;
                    indexFarthest = index;
                }
            }

            if (maxDistance > tolerance && indexFarthest != 0)
            {
                //Add the largest point that exceeds the tolerance
                pointIndexsToKeep.Add(indexFarthest);

                DouglasPeuckerReduction(points, firstPoint,
                indexFarthest, tolerance, ref pointIndexsToKeep);
                DouglasPeuckerReduction(points, indexFarthest,
                lastPoint, tolerance, ref pointIndexsToKeep);
            }
        }

        /// <span class="code-SummaryComment"><summary></span>
        /// The distance of a point from a line made from point1 and point2.
        /// <span class="code-SummaryComment"></summary></span>
        /// <span class="code-SummaryComment"><param name="pt1">The PT1.</param></span>
        /// <span class="code-SummaryComment"><param name="pt2">The PT2.</param></span>
        /// <span class="code-SummaryComment"><param name="p">The p.</param></span>
        /// <span class="code-SummaryComment"><returns></returns></span>
        public static Double PerpendicularDistance
            (Point Point1, Point Point2, Point Point)
        {
            //Vector n = Point2 - Point1;

            //Vector ap = Point1 - Point;

            //return (ap - (Vector.Multiply(ap, n) * n)).LengthSquared;
            
            //Area = |(1/2)(x1y2 + x2y3 + x3y1 - x2y1 - x3y2 - x1y3)|   *Area of triangle
            //Base = v((x1-x2)²+(x1-x2)²)                               *Base of Triangle*
            //Area = .5*Base*H                                          *Solve for height
            //Height = Area/.5/Base

            Double area = Math.Abs(.5 * (Point1.X * Point2.Y + Point2.X *
                Point.Y + Point.X * Point1.Y - Point2.X * Point1.Y - Point.X *
                Point2.Y - Point1.X * Point.Y));
            Double bottom = Math.Sqrt(Math.Pow(Point1.X - Point2.X, 2) + Math.Pow(Point1.Y - Point2.Y, 2));
            Double height = area / bottom * 2;

            return height;

            //Another option
            //Double A = Point.X - Point1.X;
            //Double B = Point.Y - Point1.Y;
            //Double C = Point2.X - Point1.X;
            //Double D = Point2.Y - Point1.Y;

            //Double dot = A * C + B * D;
            //Double len_sq = C * C + D * D;
            //Double param = dot / len_sq;

            //Double xx, yy;

            //if (param < 0)
            //{
            //    xx = Point1.X;
            //    yy = Point1.Y;
            //}
            //else if (param > 1)
            //{
            //    xx = Point2.X;
            //    yy = Point2.Y;
            //}
            //else
            //{
            //    xx = Point1.X + param * C;
            //    yy = Point1.Y + param * D;
            //}

            //Double d = DistanceBetweenOn2DPlane(Point, new Point(xx, yy));
        }
    }
}