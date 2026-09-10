using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Caelix.Rendering.RayQuery;

namespace Caelix.Debugging
{
    /// <summary>
    /// Records a fixed number of frames of GPU and CPU timings plus the ray query renderer's
    /// counters, then writes them to one JSON file.
    /// </summary>
    /// <remarks>
    /// The measurement tool of the configurable-render-group milestone: start a run, let it settle,
    /// let it sample, read the file. It owns no GPU resources and depends on nothing outside the
    /// runtime assembly.
    /// <para>
    /// GPU timings come from <see cref="FrameTimingManager"/>, which needs "Frame Timing Stats" in
    /// Player Settings (or the Development Build flag) to report anything. It works in the Editor by
    /// default on 6000.x, but verify <c>gpuSamples &gt; 0</c> in the output before trusting a run.
    /// </para>
    /// </remarks>
    public sealed class CaelixFrameProbe : MonoBehaviour
    {
        /// <summary>The host whose tick timings are recorded. Resolved from <see cref="CaelixHost.Current"/> when null.</summary>
        public CaelixHost host;

        /// <summary>The renderer whose counters are recorded. Found in the scene when null.</summary>
        public CaelixRayQueryRenderer renderer;

        /// <summary>True between <see cref="Begin"/> and the write.</summary>
        public bool IsRunning { get; private set; }

        /// <summary>True once a run has finished, whether it wrote a file or failed.</summary>
        public bool Done { get; private set; }

        /// <summary>Path the last run wrote, or null.</summary>
        public string LastPath { get; private set; }

        /// <summary>Message of the exception the last run hit, or null.</summary>
        public string LastError { get; private set; }

        private readonly List<FrameSample> samples = new();
        private readonly FrameTiming[] timingBuffer = new FrameTiming[1];

        private string outputPath;
        private string runLabel;
        private int settleRemaining;
        private int sampleTarget;

        /// <summary>One recorded frame.</summary>
        private struct FrameSample
        {
            public int frame;
            public double gpuFrameTime;
            public double cpuFrameTime;
            public double cpuMainThreadFrameTime;
            public double cpuRenderThreadFrameTime;

            public int serverTicks;
            public double tickTotalMs;
            public double clientMs;
            public double renderingMs;

            public int instanceCount;
            public int groupCount;
            public int groupsEmitted;
            public int groupsVisited;
            public int bricksStaged;
            public int aabbReallocs;
            public int instanceRebuilds;
            public int poolLiveBricks;
            public int poolCapacityBricks;
            public long poolVramBytes;
            public long aabbVramBytes;
        }

        /// <summary>
        /// Starts a run: skips <paramref name="settleFrames"/> frames, records
        /// <paramref name="sampleFrames"/> frames, then writes the JSON to <paramref name="path"/>.
        /// </summary>
        /// <param name="path">Absolute or project-relative file path. Missing directories are created.</param>
        /// <param name="settleFrames">Frames to discard first, so a warm-up is not measured.</param>
        /// <param name="sampleFrames">Frames to record. At least 1.</param>
        /// <param name="label">Free-form name written into the file.</param>
        public void Begin(string path, int settleFrames, int sampleFrames, string label)
        {
            outputPath = path;
            runLabel = label;
            settleRemaining = Mathf.Max(0, settleFrames);
            sampleTarget = Mathf.Max(1, sampleFrames);

            samples.Clear();
            samples.Capacity = Mathf.Max(samples.Capacity, sampleTarget);

            LastPath = null;
            LastError = null;
            Done = false;
            IsRunning = true;
        }

        private void LateUpdate()
        {
            if (!IsRunning)
            {
                return;
            }

            if (settleRemaining > 0)
            {
                settleRemaining--;
                return;
            }

            samples.Add(CaptureFrame());

            if (samples.Count >= sampleTarget)
            {
                IsRunning = false;
                Write();
                Done = true;
            }
        }

        private FrameSample CaptureFrame()
        {
            var sample = new FrameSample { frame = Time.frameCount };

            // CaptureFrameTimings publishes what the driver has finished; GetLatestTimings returns 0
            // entries while the pipeline is still filling up, which is what settleFrames is for.
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, timingBuffer) > 0)
            {
                FrameTiming t = timingBuffer[0];
                sample.gpuFrameTime = t.gpuFrameTime;
                sample.cpuFrameTime = t.cpuFrameTime;
                sample.cpuMainThreadFrameTime = t.cpuMainThreadFrameTime;
                sample.cpuRenderThreadFrameTime = t.cpuRenderThreadFrameTime;
            }

            CaelixHost h = ResolveHost();
            if (h != null)
            {
                CaelixHost.HostTimingStats timings = h.LastTickTimings;
                sample.serverTicks = timings.ServerTicks;
                sample.tickTotalMs = timings.TotalMilliseconds;
                sample.clientMs = timings.ClientMilliseconds;
                sample.renderingMs = timings.RenderingMilliseconds;
            }

            CaelixRayQueryRenderer r = ResolveRenderer();
            if (r != null)
            {
                sample.instanceCount = r.instanceCount;
                sample.groupCount = r.groupCount;
                sample.groupsEmitted = r.groupsEmittedThisTick;
                sample.groupsVisited = r.groupsVisitedThisTick;
                sample.bricksStaged = r.bricksStagedThisTick;
                sample.aabbReallocs = r.aabbReallocsThisTick;
                sample.instanceRebuilds = r.instanceRebuildsThisTick;
                sample.poolLiveBricks = r.poolLiveBricks;
                sample.poolCapacityBricks = r.poolCapacityBricks;
                sample.poolVramBytes = r.poolVramBytes;
                sample.aabbVramBytes = r.aabbVramBytes;
            }

            return sample;
        }

        private CaelixHost ResolveHost()
        {
            if (host == null)
            {
                host = CaelixHost.Current;
            }

            return host;
        }

        private CaelixRayQueryRenderer ResolveRenderer()
        {
            if (renderer == null)
            {
                renderer = FindFirstObjectByType<CaelixRayQueryRenderer>();
            }

            return renderer;
        }

        private void Write()
        {
            try
            {
                string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(outputPath, BuildJson(), Encoding.UTF8);
                LastPath = outputPath;
            }
            catch (Exception e)
            {
                LastError = e.Message;
                Debug.LogError($"CaelixFrameProbe: could not write '{outputPath}': {e}", this);
            }
        }

        private string BuildJson()
        {
            CaelixRayQueryRenderer r = ResolveRenderer();
            var gpu = new List<double>();
            for (int i = 0; i < samples.Count; i++)
            {
                if (samples[i].gpuFrameTime > 0) gpu.Add(samples[i].gpuFrameTime);
            }

            var sb = new StringBuilder(1024 + samples.Count * 256);
            sb.Append('{');
            Str(sb, "label", runLabel ?? string.Empty).Append(',');
            Str(sb, "groupSize", r != null ? r.GroupSize.ToString() : "unknown").Append(',');
            Num(sb, "width", Screen.width).Append(',');
            Num(sb, "height", Screen.height).Append(',');
            Num(sb, "sampleFrames", samples.Count).Append(',');
            Num(sb, "gpuSamples", gpu.Count).Append(',');

            sb.Append("\"summary\":{");
            MeanP95(sb, "gpuMs", gpu).Append(',');
            MeanP95(sb, "cpuMs", Select(s => s.cpuFrameTime)).Append(',');
            MeanP95(sb, "mainThreadMs", Select(s => s.cpuMainThreadFrameTime)).Append(',');
            MeanP95(sb, "renderingMs", Select(s => s.renderingMs)).Append(',');
            MeanMax(sb, "groupsEmitted", Select(s => (double)s.groupsEmitted)).Append(',');
            MeanMax(sb, "groupsVisited", Select(s => (double)s.groupsVisited)).Append(',');
            MeanMax(sb, "bricksStaged", Select(s => (double)s.bricksStaged)).Append(',');
            MeanMax(sb, "aabbReallocs", Select(s => (double)s.aabbReallocs)).Append(',');
            MeanMax(sb, "instanceRebuilds", Select(s => (double)s.instanceRebuilds)).Append(',');

            FrameSample last = samples.Count > 0 ? samples[samples.Count - 1] : default;
            Num(sb, "instanceCount", last.instanceCount).Append(',');
            Num(sb, "groupCount", last.groupCount).Append(',');
            Num(sb, "poolLiveBricks", last.poolLiveBricks).Append(',');
            Num(sb, "poolCapacityBricks", last.poolCapacityBricks).Append(',');
            Num(sb, "poolVramBytes", last.poolVramBytes).Append(',');
            Num(sb, "aabbVramBytes", last.aabbVramBytes);
            sb.Append("},");

            sb.Append("\"frames\":[");
            for (int i = 0; i < samples.Count; i++)
            {
                if (i > 0) sb.Append(',');
                AppendFrame(sb, samples[i]);
            }

            sb.Append("]}");
            return sb.ToString();
        }

        private static void AppendFrame(StringBuilder sb, in FrameSample s)
        {
            sb.Append('{');
            Num(sb, "frame", s.frame).Append(',');
            Num(sb, "gpuFrameTime", s.gpuFrameTime).Append(',');
            Num(sb, "cpuFrameTime", s.cpuFrameTime).Append(',');
            Num(sb, "cpuMainThreadFrameTime", s.cpuMainThreadFrameTime).Append(',');
            Num(sb, "cpuRenderThreadFrameTime", s.cpuRenderThreadFrameTime).Append(',');
            Num(sb, "serverTicks", s.serverTicks).Append(',');
            Num(sb, "tickTotalMs", s.tickTotalMs).Append(',');
            Num(sb, "clientMs", s.clientMs).Append(',');
            Num(sb, "renderingMs", s.renderingMs).Append(',');
            Num(sb, "instanceCount", s.instanceCount).Append(',');
            Num(sb, "groupCount", s.groupCount).Append(',');
            Num(sb, "groupsEmitted", s.groupsEmitted).Append(',');
            Num(sb, "groupsVisited", s.groupsVisited).Append(',');
            Num(sb, "bricksStaged", s.bricksStaged).Append(',');
            Num(sb, "aabbReallocs", s.aabbReallocs).Append(',');
            Num(sb, "instanceRebuilds", s.instanceRebuilds).Append(',');
            Num(sb, "poolLiveBricks", s.poolLiveBricks).Append(',');
            Num(sb, "poolCapacityBricks", s.poolCapacityBricks).Append(',');
            Num(sb, "poolVramBytes", s.poolVramBytes).Append(',');
            Num(sb, "aabbVramBytes", s.aabbVramBytes);
            sb.Append('}');
        }

        private List<double> Select(Func<FrameSample, double> pick)
        {
            var values = new List<double>(samples.Count);
            for (int i = 0; i < samples.Count; i++)
            {
                values.Add(pick(samples[i]));
            }

            return values;
        }

        private static StringBuilder MeanP95(StringBuilder sb, string name, List<double> values)
        {
            sb.Append('"').Append(name).Append("\":{");
            Num(sb, "mean", Mean(values)).Append(',');
            Num(sb, "p95", Percentile95(values));
            return sb.Append('}');
        }

        private static StringBuilder MeanMax(StringBuilder sb, string name, List<double> values)
        {
            sb.Append('"').Append(name).Append("\":{");
            Num(sb, "mean", Mean(values)).Append(',');
            Num(sb, "max", Max(values));
            return sb.Append('}');
        }

        private static double Mean(List<double> values)
        {
            if (values.Count == 0) return 0;
            double sum = 0;
            for (int i = 0; i < values.Count; i++) sum += values[i];
            return sum / values.Count;
        }

        private static double Max(List<double> values)
        {
            double max = 0;
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i] > max) max = values[i];
            }

            return max;
        }

        /// <summary>Value at index ceil(0.95 * n) - 1 of the sorted samples; 0 for no samples.</summary>
        private static double Percentile95(List<double> values)
        {
            if (values.Count == 0) return 0;
            var sorted = new List<double>(values);
            sorted.Sort();
            int index = Mathf.CeilToInt(0.95f * sorted.Count) - 1;
            index = Mathf.Clamp(index, 0, sorted.Count - 1);
            return sorted[index];
        }

        private static StringBuilder Num(StringBuilder sb, string name, double value)
            => sb.Append('"').Append(name).Append("\":")
                 .Append(value.ToString("R", CultureInfo.InvariantCulture));

        private static StringBuilder Num(StringBuilder sb, string name, long value)
            => sb.Append('"').Append(name).Append("\":")
                 .Append(value.ToString(CultureInfo.InvariantCulture));

        private static StringBuilder Str(StringBuilder sb, string name, string value)
            => sb.Append('"').Append(name).Append("\":\"").Append(Escape(value)).Append('"');

        private static string Escape(string value)
            => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
