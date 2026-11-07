namespace AzureGameAssistant.Ai;

/// <summary>
/// Deterministic fallback report. Used when the model is unavailable, returns
/// invalid JSON twice, or the user stays offline. It only cites metrics that
/// are present in the fact layer.
/// </summary>
public static class LocalReportBuilder
{
    public static AiReport Build(FactsDocument facts)
    {
        bool chinese = (facts.Locale ?? string.Empty).StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        var report = new AiReport
        {
            PrimaryCause = DetermineCause(facts, out string causeReason),
            Confidence = facts.Frames is null ? "low" : "medium",
        };

        report.Summary = BuildSummary(facts, causeReason, chinese);
        report.Evidence = BuildEvidence(facts, chinese);
        report.Suspects = BuildSuspects(facts, chinese);
        report.FrameAnalysis = BuildFrameAnalysis(facts, chinese);
        report.Recommendations = BuildRecommendations(facts, chinese);
        report.Limitations =
        [
            chinese
                ? "本地规则报告只基于窗口内的聚合指标，不包含游戏内设置与场景上下文"
                : "The local report uses aggregated metrics only and has no in-game context",
            chinese
                ? facts.Frames is null
                    ? "窗口内没有帧数据，帧节奏判断受限"
                    : "帧判断基于已采集的帧记录，未采集的时段不在分析范围内"
                : facts.Frames is null
                    ? "No frame data was available in the window"
                    : "Frame analysis covers only the captured frames",
        ];
        report.Disclaimer = chinese
            ? "本报告由本地规则生成，未联网；建议仅供参考，请自行评估风险。"
            : "Generated locally without network access. Recommendations are advisory; assess risks yourself.";

        return report;
    }

    private static string DetermineCause(FactsDocument facts, out string reason)
    {
        bool chinese = (facts.Locale ?? string.Empty).StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        Metrics metrics = Metrics.From(facts);

        if (metrics.VramRatio is >= 0.92)
        {
            reason = chinese ? "显存占用接近上限" : "VRAM usage is near capacity";
            return "VramPressure";
        }

        if (metrics.GpuTemperature is >= 85 || metrics.GpuHotSpot is >= 95)
        {
            reason = chinese ? "GPU 温度接近降频区间" : "GPU temperature is close to throttling";
            return "ThermalThrottle";
        }

        if (metrics.MemAvailableMb is < 1_024 || metrics.CommitRatio is >= 0.9)
        {
            reason = chinese ? "可用内存紧张或提交率过高" : "Available memory is low or commit ratio is high";
            return "MemoryPressure";
        }

        if (metrics.DiskLatencyMs is >= 50)
        {
            reason = chinese ? "磁盘响应时间偏高" : "Disk latency is high";
            return "DiskBound";
        }

        if (metrics.OnePercentLowFps > 0 && metrics.AvgFps > 0 &&
            metrics.OnePercentLowFps < metrics.AvgFps * 0.5)
        {
            reason = chinese ? "平均帧与低帧差距明显" : "Average FPS and low FPS diverge sharply";
            return "FramePacing";
        }

        if (metrics.CpuPeakPct is >= 85)
        {
            reason = chinese ? "CPU 峰值占用很高" : "CPU peak utilisation is very high";
            return "CpuContention";
        }

        if (metrics.GpuPeakPct is >= 90 && metrics.CpuAvgPct is < 80)
        {
            reason = chinese ? "GPU 长时间高负载" : "GPU is continuously busy";
            return "GpuBound";
        }

        reason = chinese ? "聚合指标未命中明确瓶颈" : "Aggregated metrics show no clear bottleneck";
        return "Unknown";
    }

    private static string BuildSummary(FactsDocument facts, string reason, bool chinese)
    {
        string cpu = Format(facts.Metrics.CpuPeakPct, "%");
        string gpu = Format(facts.Metrics.GpuPeakPct, "%");
        string fps = Format(facts.Frames?.AvgFps, " FPS");

        return chinese
            ? $"CPU 峰值 {cpu}、GPU 峰值 {gpu}、平均帧 {fps}；主要判断：{reason}。"
            : $"CPU peak {cpu}, GPU peak {gpu}, average {fps}; main finding: {reason}.";
    }

    private static List<AiEvidence> BuildEvidence(FactsDocument facts, bool chinese)
    {
        var evidence = new List<AiEvidence>();
        bool zh = chinese;

        void Add(string metric, double? value, string unit, string interpretation)
        {
            if (value is null)
            {
                return;
            }

            evidence.Add(new AiEvidence
            {
                Metric = metric,
                Value = value.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + unit,
                Interpretation = interpretation,
            });
        }

        Add("cpuPeakPct", facts.Metrics.CpuPeakPct, "%", zh ? "CPU 峰值占用" : "CPU peak utilisation");
        Add("gpuPeakPct", facts.Metrics.GpuPeakPct, "%", zh ? "GPU 峰值占用" : "GPU peak utilisation");
        Add("memAvailableMb", facts.Metrics.MemAvailableMb, " MB", zh ? "窗口内最低可用内存" : "Lowest available memory in the window");
        Add("commitRatio", facts.Metrics.CommitRatio, "", zh ? "最高提交率" : "Highest commit ratio");
        Add("diskLatencyMs", facts.Metrics.DiskLatencyMs, " ms", zh ? "磁盘峰值响应时间" : "Peak disk latency");

        if (facts.Frames is { } frames)
        {
            Add("avgFps", frames.AvgFps, "", zh ? "窗口平均帧率" : "Average FPS in the window");
            Add("onePercentLowFps", frames.OnePercentLowFps, "", zh ? "1% Low 帧率" : "1% low FPS");
            Add("maxFrameTimeMs", frames.MaxFrameTimeMs, " ms", zh ? "最长帧时间" : "Longest frame time");
        }

        if (facts.Sensors is { } sensors)
        {
            Add("gpuTemperatureC", sensors.GpuTemperatureC, " °C", zh ? "GPU 当前温度" : "Current GPU temperature");
            Add("cpuTemperatureC", sensors.CpuTemperatureC, " °C", zh ? "CPU 当前温度" : "Current CPU temperature");
        }

        return evidence;
    }

    private static List<AiSuspect> BuildSuspects(FactsDocument facts, bool chinese)
    {
        return facts.Processes
            .Where(process => process.CpuPeakPct is >= 10 || process.GpuPeakPct is >= 10)
            .Take(5)
            .Select(process => new AiSuspect
            {
                Name = process.Name,
                Role = process.Role,
                Reason = chinese
                    ? $"CPU 峰值 {Format(process.CpuPeakPct, "%")}，GPU 峰值 {Format(process.GpuPeakPct, "%")}"
                    : $"CPU peak {Format(process.CpuPeakPct, "%")}, GPU peak {Format(process.GpuPeakPct, "%")}",
            })
            .ToList();
    }

    private static AiFrameAnalysis BuildFrameAnalysis(FactsDocument facts, bool chinese)
    {
        if (facts.Frames is not { } frames)
        {
            return new AiFrameAnalysis
            {
                Summary = chinese ? "窗口内没有帧数据" : "No frame data in the window",
            };
        }

        bool pacing = frames.AvgFps is > 0 &&
                      frames.OnePercentLowFps is > 0 &&
                      frames.OnePercentLowFps < frames.AvgFps * 0.6;
        bool cpuBound = frames.AvgCpuWaitMs is > 0 &&
                        (frames.AvgGpuWaitMs is null || frames.AvgCpuWaitMs > frames.AvgGpuWaitMs * 1.5);

        return new AiFrameAnalysis
        {
            Summary = chinese
                ? $"平均 {Format(frames.AvgFps, " FPS")}，1% Low {Format(frames.OnePercentLowFps, " FPS")}，最长帧 {Format(frames.MaxFrameTimeMs, " ms")}"
                : $"Average {Format(frames.AvgFps, " FPS")}, 1% low {Format(frames.OnePercentLowFps, " FPS")}, longest frame {Format(frames.MaxFrameTimeMs, " ms")}",
            CpuBound = cpuBound,
            GpuBound = !cpuBound && facts.Metrics.GpuPeakPct is >= 85,
            PacingIssue = pacing,
        };
    }

    private static List<AiRecommendation> BuildRecommendations(FactsDocument facts, bool chinese)
    {
        var recommendations = new List<AiRecommendation>();

        void Add(string action, string risk, string effect, string howTo, string? rollback = null) =>
            recommendations.Add(new AiRecommendation
            {
                Action = action,
                Risk = risk,
                ExpectedEffect = effect,
                HowTo = howTo,
                Rollback = rollback,
            });

        if (chinese)
        {
            Add(
                "关闭不必要的后台程序",
                "low",
                "释放 CPU、内存与磁盘带宽，减少帧时间抖动",
                "在任务管理器中按 CPU 与内存排序，结束不需要的后台应用后重新观测。");

            Add(
                "降低游戏画质或分辨率",
                "low",
                "降低 GPU 与显存压力，通常能提升低帧表现",
                "优先降低阴影、体积雾、纹理质量与渲染分辨率，逐项调整并对比 1% Low。");

            if (facts.Metrics.VramUsedMb is not null)
            {
                Add(
                    "关闭高清材质包或降低纹理质量",
                    "low",
                    "减少显存占用，避免显存压力导致的卡顿",
                    "在游戏图形设置中关闭高清材质包，或将纹理质量下调一档。");
            }

            if (facts.Metrics.GpuPeakPct is >= 85)
            {
                Add(
                    "确认显卡散热与风扇曲线",
                    "medium",
                    "避免温度过高触发降频",
                    "检查机箱进出风与风扇转速；只调整厂商工具中的风扇曲线，不改电压。",
                    "恢复风扇曲线默认值。");
            }

            Add(
                "更新显卡驱动并检查电源计划",
                "low",
                "修复已知调度问题，保证高负载下频率稳定",
                "从显卡厂商官网安装正式版驱动；把 Windows 电源计划设为“平衡”或“高性能”后复测。");
        }
        else
        {
            Add(
                "Close unnecessary background applications",
                "low",
                "Frees CPU, memory and disk bandwidth and reduces frame-time jitter",
                "Sort Task Manager by CPU and memory, end unneeded background apps, then re-measure.");

            Add(
                "Lower in-game graphics settings or resolution",
                "low",
                "Reduces GPU and VRAM pressure, usually improving low FPS",
                "Lower shadows, volumetric fog, texture quality and render resolution one step at a time, comparing 1% low.");

            if (facts.Metrics.VramUsedMb is not null)
            {
                Add(
                    "Disable the HD texture pack or lower texture quality",
                    "low",
                    "Lowers VRAM usage and avoids VRAM-induced stutter",
                    "Disable the HD texture pack in the game settings or drop texture quality by one level.");
            }

            if (facts.Metrics.GpuPeakPct is >= 85)
            {
                Add(
                    "Verify GPU cooling and fan curve",
                    "medium",
                    "Avoids thermal throttling under sustained load",
                    "Check case airflow and fan speed; adjust only the vendor fan curve, never voltage.",
                    "Restore the default fan curve.");
            }

            Add(
                "Update the GPU driver and check the power plan",
                "low",
                "Fixes known scheduling issues and keeps clocks stable under load",
                "Install the stable driver from the GPU vendor; set Windows power plan to Balanced or High performance, then re-test.");
        }

        return recommendations.Take(5).ToList();
    }

    private static string Format(double? value, string suffix) =>
        value is null
            ? "—"
            : value.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + suffix;

    private sealed record Metrics(
        double? VramRatio,
        double? GpuTemperature,
        double? GpuHotSpot,
        double? MemAvailableMb,
        double? CommitRatio,
        double? DiskLatencyMs,
        double? CpuPeakPct,
        double? CpuAvgPct,
        double? GpuPeakPct,
        double? AvgFps,
        double? OnePercentLowFps)
    {
        public static Metrics From(FactsDocument facts)
        {
            double? vramRatio = facts.Metrics.VramUsedMb is { } used &&
                                facts.Metrics.VramTotalMb is { } total &&
                                total > 0
                ? used / total
                : null;

            return new Metrics(
                vramRatio,
                facts.Sensors?.GpuTemperatureC,
                facts.Sensors?.GpuHotSpotC,
                facts.Metrics.MemAvailableMb,
                facts.Metrics.CommitRatio,
                facts.Metrics.DiskLatencyMs,
                facts.Metrics.CpuPeakPct,
                facts.Metrics.CpuAvgPct,
                facts.Metrics.GpuPeakPct,
                facts.Frames?.AvgFps,
                facts.Frames?.OnePercentLowFps);
        }
    }
}
